using System;
using System.Windows.Threading;

namespace DynamicIsland.Timer
{
    public enum AppleTimerState
    {
        Inactive,
        Running,
        Paused,
        Completed
    }

    public class AppleTimerManager
    {
        public static AppleTimerManager Instance { get; } = new AppleTimerManager();

        private readonly DispatcherTimer tickerTimer = new DispatcherTimer();
        private TimeSpan totalDuration = TimeSpan.FromMinutes(5);
        private TimeSpan remainingDuration = TimeSpan.FromMinutes(5);
        private DateTimeOffset targetEndTime = DateTimeOffset.Now;
        private AppleTimerState state = AppleTimerState.Inactive;

        public event Action<AppleTimerState, TimeSpan, double>? OnTimerTick;
        public event Action? OnTimerCompleted;

        public AppleTimerState State => state;
        public TimeSpan TotalDuration => totalDuration;
        public TimeSpan RemainingDuration => remainingDuration;
        public bool IsActive => state != AppleTimerState.Inactive;
        public int CustomDurationMinutes { get; set; } = 5;

        public AppleTimerManager()
        {
            tickerTimer.Interval = TimeSpan.FromMilliseconds(250);
            tickerTimer.Tick += TickerTimer_Tick;
        }

        public void StartTimer(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(5);

            totalDuration = duration;
            remainingDuration = duration;
            targetEndTime = DateTimeOffset.Now + duration;
            state = AppleTimerState.Running;

            tickerTimer.Start();
            NotifyUpdate();
        }

        public void StartPreset(int minutes)
        {
            if (minutes <= 0) minutes = 5;
            CustomDurationMinutes = minutes;
            StartTimer(TimeSpan.FromMinutes(minutes));
        }

        public void AdjustCustomMinutes(int delta)
        {
            CustomDurationMinutes += delta;
            if (CustomDurationMinutes < 1) CustomDurationMinutes = 1;
            if (CustomDurationMinutes > 180) CustomDurationMinutes = 180;
        }

        public void AddTimeToRunningTimer(TimeSpan extraTime)
        {
            if (state == AppleTimerState.Running || state == AppleTimerState.Paused)
            {
                totalDuration += extraTime;
                remainingDuration += extraTime;
                targetEndTime += extraTime;
                NotifyUpdate();
            }
        }

        public void TogglePauseResume()
        {
            if (state == AppleTimerState.Running)
            {
                remainingDuration = targetEndTime - DateTimeOffset.Now;
                if (remainingDuration < TimeSpan.Zero) remainingDuration = TimeSpan.Zero;
                state = AppleTimerState.Paused;
                tickerTimer.Stop();
                NotifyUpdate();
            }
            else if (state == AppleTimerState.Paused)
            {
                targetEndTime = DateTimeOffset.Now + remainingDuration;
                state = AppleTimerState.Running;
                tickerTimer.Start();
                NotifyUpdate();
            }
            else if (state == AppleTimerState.Completed || state == AppleTimerState.Inactive)
            {
                StartTimer(CustomDurationMinutes > 0 ? TimeSpan.FromMinutes(CustomDurationMinutes) : TimeSpan.FromMinutes(5));
            }
        }

        public void StopTimer()
        {
            state = AppleTimerState.Inactive;
            tickerTimer.Stop();
            remainingDuration = TimeSpan.Zero;
            NotifyUpdate();
        }

        private void TickerTimer_Tick(object? sender, EventArgs e)
        {
            if (state == AppleTimerState.Running)
            {
                var remaining = targetEndTime - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                    remainingDuration = TimeSpan.Zero;
                    state = AppleTimerState.Completed;
                    tickerTimer.Stop();
                    NotifyUpdate();
                    OnTimerCompleted?.Invoke();
                }
                else
                {
                    remainingDuration = remaining;
                    NotifyUpdate();
                }
            }
        }

        private void NotifyUpdate()
        {
            double progress = 0.0;
            if (totalDuration.TotalSeconds > 0)
            {
                progress = Math.Clamp(remainingDuration.TotalSeconds / totalDuration.TotalSeconds, 0.0, 1.0);
            }
            OnTimerTick?.Invoke(state, remainingDuration, progress);
        }

        public static string FormatTimerText(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return t.ToString(@"h\:mm\:ss");
            return t.ToString(@"m\:ss");
        }
    }
}
