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
        private DateTime lastDataTime = DateTime.MinValue;

        public bool HasLiveAudio => (DateTime.UtcNow - lastDataTime).TotalMilliseconds < 350;

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
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Spectrum] WasapiLoopbackCapture failed: {ex.Message}");
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

            // Extract float samples from 32-bit Float, 16-bit PCM, or 24-bit PCM
            if (format.BitsPerSample == 32)
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
            else if (format.BitsPerSample == 24)
            {
                int sampleCount = e.BytesRecorded / 3;
                for (int i = 0; i < sampleCount; i += channels)
                {
                    int offset = i * 3;
                    int val = (e.Buffer[offset] << 8) | (e.Buffer[offset + 1] << 16) | (e.Buffer[offset + 2] << 24);
                    audioBuffer[bufferPos] = val / 2147483648.0f;
                    bufferPos = (bufferPos + 1) % audioBuffer.Length;
                }
            }

            // Process real frequency bands (Kick Bass, Low Mids, High Mids, Treble)
            ProcessSpectrumBands(sampleRate);
        }

        private void ProcessSpectrumBands(int sampleRate)
        {
            int n = audioBuffer.Length;

            // Multi-frequency sampling per band for rich, responsive spectrum capture across all musical keys
            float[][] bandFrequencies = new float[][]
            {
                new float[] { 50f, 75f, 110f, 150f },         // Band 0: Sub-bass & Kick Drops (20 - 180 Hz)
                new float[] { 220f, 320f, 480f, 650f },       // Band 1: Vocals & Snares (180 - 750 Hz)
                new float[] { 900f, 1400f, 2100f, 2900f },    // Band 2: Melodies & Guitars (750 - 3200 Hz)
                new float[] { 4200f, 6800f, 9500f, 13000f }   // Band 3: Hi-hats & Cymbals (3200 - 14000 Hz)
            };

            for (int b = 0; b < 4; b++)
            {
                double maxMag = 0.0;
                foreach (float freq in bandFrequencies[b])
                {
                    double mag = CalculateGoertzelMagnitude(audioBuffer, freq, sampleRate, n);
                    if (mag > maxMag) maxMag = mag;
                }

                // Apple-tuned loudness scaling and dynamic sensitivity boost
                double boost = b == 0 ? 5.2 : (b == 1 ? 3.8 : (b == 2 ? 4.2 : 6.0));
                double energy = Math.Clamp(maxMag * boost, 0.0, 1.0);

                // Punchy logarithmic curve for visible bounce
                energy = Math.Pow(energy, 0.55);

                // Instant snappy attack on beat drop + smooth decay
                if (energy > peakEnergies[b])
                {
                    peakEnergies[b] = energy; // Instant attack
                }
                else
                {
                    peakEnergies[b] -= (peakEnergies[b] - energy) * 0.22; // Smooth natural decay
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
                // Hann window to isolate frequencies cleanly
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
