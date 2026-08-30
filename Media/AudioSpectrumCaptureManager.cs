using System;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DynamicIsland.Media
{
    public class AudioSpectrumCaptureManager : IDisposable
    {
        private static AudioSpectrumCaptureManager? instance;
        public static AudioSpectrumCaptureManager Instance => instance ??= new AudioSpectrumCaptureManager();

        private WasapiLoopbackCapture? capture;
        private readonly object captureLock = new object();
        private bool isRunning = false;

        public double[] BandEnergies { get; } = new double[6]; // 4-6 bands
        private readonly double[] peakEnergies = new double[6];
        public event Action<double[]>? OnSpectrumUpdated;

        private float[] audioBuffer = new float[1024];
        private int bufferPos = 0;

        private DateTime lastDataTime = DateTime.UtcNow;

        public void Start()
        {
            lock (captureLock)
            {
                if (isRunning) return;

                try
                {
                    capture = new WasapiLoopbackCapture();
                    capture.DataAvailable += Capture_DataAvailable;
                    capture.RecordingStopped += Capture_RecordingStopped;
                    capture.StartRecording();
                    isRunning = true;
                }
                catch
                {
                    isRunning = false;
                    capture?.Dispose();
                    capture = null;
                }
            }
        }

        public void Stop()
        {
            lock (captureLock)
            {
                if (!isRunning) return;
                isRunning = false;

                try
                {
                    capture?.StopRecording();
                    capture?.Dispose();
                    capture = null;
                }
                catch { }

                for (int i = 0; i < BandEnergies.Length; i++)
                {
                    BandEnergies[i] = 0.0;
                    peakEnergies[i] = 0.0;
                }
            }
        }

        private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (captureLock)
            {
                isRunning = false;
            }
        }

        private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || capture == null) return;

            var format = capture.WaveFormat;
            int channels = Math.Max(1, format.Channels);
            int sampleRate = format.SampleRate > 0 ? format.SampleRate : 48000;

            // Extract float samples from IEEE float 32-bit or 16-bit PCM
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                int floatCount = e.BytesRecorded / 4;
                for (int i = 0; i < floatCount; i += channels)
                {
                    float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                    audioBuffer[bufferPos] = sample;
                    bufferPos = (bufferPos + 1) % audioBuffer.Length;
                }
            }
            else if (format.BitsPerSample == 16)
            {
                int sampleCount = e.BytesRecorded / 2;
                for (int i = 0; i < sampleCount; i += channels)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                    audioBuffer[bufferPos] = sample / 32768.0f;
                    bufferPos = (bufferPos + 1) % audioBuffer.Length;
                }
            }

            // Calculate 4 real-time frequency spectrum bands using discrete Goertzel / band filters
            ProcessSpectrumBands(sampleRate);
        }

        private void ProcessSpectrumBands(int sampleRate)
        {
            int n = audioBuffer.Length;
            // Key center frequencies: Sub-bass (65Hz), Bass/Mids (250Hz), High-Mids (1200Hz), Treble (4500Hz)
            float[] targetFreqs = new float[] { 70f, 260f, 1100f, 4200f, 8500f, 13000f };

            for (int b = 0; b < 4; b++)
            {
                float freq = targetFreqs[b];
                double mag = CalculateGoertzelMagnitude(audioBuffer, freq, sampleRate, n);

                // Boost low bass and treble for punchy Apple-like visualizer responsiveness
                double boost = b == 0 ? 3.8 : (b == 1 ? 2.6 : (b == 2 ? 3.0 : 4.5));
                double energy = Math.Clamp(mag * boost, 0.0, 1.0);

                // Non-linear loudness curve for dynamic movement across quiet & loud sections
                energy = Math.Pow(energy, 0.65);

                // Instant attack (punchy on beat drop) & smooth decay
                if (energy > peakEnergies[b])
                {
                    peakEnergies[b] = energy; // Instant attack
                }
                else
                {
                    peakEnergies[b] -= (peakEnergies[b] - energy) * 0.18; // Smooth decay
                }

                BandEnergies[b] = Math.Clamp(peakEnergies[b], 0.0, 1.0);
            }

            lastDataTime = DateTime.UtcNow;
            OnSpectrumUpdated?.Invoke(BandEnergies);
        }

        private double CalculateGoertzelMagnitude(float[] samples, float targetFreq, int sampleRate, int numSamples)
        {
            double k = 0.5 + ((numSamples * targetFreq) / sampleRate);
            double omega = (2.0 * Math.PI * k) / numSamples;
            double cosine = Math.Cos(omega);
            double coeff = 2.0 * cosine;

            double q0 = 0, q1 = 0, q2 = 0;

            for (int i = 0; i < numSamples; i++)
            {
                // Hann window to reduce spectral leakage
                double window = 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (numSamples - 1)));
                double sample = samples[i] * window;

                q0 = coeff * q1 - q2 + sample;
                q2 = q1;
                q1 = q0;
            }

            double real = (q1 - q2 * cosine);
            double imag = (q2 * Math.Sin(omega));

            return Math.Sqrt(real * real + imag * imag) / (numSamples / 2.0);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
