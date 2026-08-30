using System;
using System.Windows.Media;

namespace DynamicIsland.Call
{
    public enum CallState
    {
        None,
        Incoming,
        OngoingVoice,
        OngoingVideo,
        Ended
    }

    public enum CallType
    {
        Voice,
        Video
    }

    public class CallInfo
    {
        public string CallerName { get; set; } = "Unknown Caller";
        public string Subtitle { get; set; } = "WhatsApp Call";
        public string AppName { get; set; } = "WhatsApp";
        public ImageSource? Avatar { get; set; }
        public CallType Type { get; set; } = CallType.Voice;
        public CallState State { get; set; } = CallState.None;
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public TimeSpan Duration => State == CallState.OngoingVoice || State == CallState.OngoingVideo 
            ? DateTime.UtcNow - StartTime 
            : TimeSpan.Zero;
        public uint? NotificationId { get; set; }
    }
}
