using System;
using System.Diagnostics;
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

        public double[] BandEnergies { get; } = new double[4];
        private readonly double[] peakEnergies = new double[4];
        private double maxObservedEnergy = 0.04;

        public event Action<double[]>? OnSpectrumUpdated;

        private readonly float[] audioBuffer = new float[1024];
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
                    Debug.WriteLine($"[Spectrum] WasapiLoopbackCapture start failed: {ex.Message}");
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

            ProcessSpectrumBands(sampleRate);
        }

        private void ProcessSpectrumBands(int sampleRate)
        {
            int n = audioBuffer.Length;

            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                sumSq += audioBuffer[i] * audioBuffer[i];
            }
            double rms = Math.Sqrt(sumSq / n);

            if (rms < 0.001)
            {
                for (int b = 0; b < 4; b++)
                {
                    peakEnergies[b] -= peakEnergies[b] * 0.10;
                    BandEnergies[b] = Math.Clamp(peakEnergies[b], 0.0, 1.0);
                }
                OnSpectrumUpdated?.Invoke(BandEnergies);
                return;
            }

            float[][] bandFrequencies = new float[][]
            {
                new float[] { 55f, 85f, 130f },        // Bar 0: Bass & Kick Drops
                new float[] { 260f, 420f, 650f },      // Bar 1: Vocals & Snares
                new float[] { 1100f, 1800f, 2600f },   // Bar 2: Melodies & Guitars
                new float[] { 5000f, 8000f, 11500f }   // Bar 3: Hi-hats & Crisp Treble
            };

            for (int b = 0; b < 4; b++)
            {
                double sumMag = 0;
                foreach (float freq in bandFrequencies[b])
                {
                    sumMag += CalculateGoertzelMagnitude(audioBuffer, freq, sampleRate, n);
                }
                double avgMag = sumMag / bandFrequencies[b].Length;

                // Automatic Gain Control with smooth dynamic ceiling
                if (avgMag > maxObservedEnergy)
                {
                    maxObservedEnergy = (maxObservedEnergy * 0.92) + (avgMag * 0.08);
                }
                else
                {
                    maxObservedEnergy = Math.Max(0.015, maxObservedEnergy * 0.999);
                }

                double normalized = Math.Clamp(avgMag / maxObservedEnergy, 0.0, 1.0);

                double boostFactor = b switch
                {
                    0 => 1.35,
                    1 => 1.20,
                    2 => 1.10,
                    _ => 1.00
                };

                double rawEnergy = Math.Clamp(Math.Pow(normalized, 0.65) * boostFactor, 0.0, 1.0);

                // Temporal low-pass filter: eliminate raw audio micro-jitter/vibration
                if (rawEnergy > peakEnergies[b])
                {
                    // Snappy rise on beat attack
                    peakEnergies[b] = (peakEnergies[b] * 0.35) + (rawEnergy * 0.65);
                }
                else
                {
                    // Gentle exponential release
                    peakEnergies[b] = (peakEnergies[b] * 0.88) + (rawEnergy * 0.12);
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
