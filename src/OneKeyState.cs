using System;

namespace RemoteDictation
{
    // 0.3.2：不再模拟豆包语音键（实测豆包不接受 SendInput 注入的 Alt）。
    // 用户在远控窗口里亲手按住豆包语音键；本程序只把前台切到本机听写框，让豆包看到仍按着的真实按键。
    internal enum HoldPhase { Idle, Listening, Finalizing, Delivering, Failed }

    internal enum FinishDecision { Wait, Deliver, Abandon, Timeout }

    internal static class HoldPolicy
    {
        // 按下瞬间就切到本机听写框；松开得很快（只是点一下 Alt）就直接撤回。
        internal const int TapMilliseconds = 500;
        internal const int ReleaseDebounceMilliseconds = 200;
        internal const int FocusGraceMilliseconds = 800;
        internal const int AbandonMilliseconds = 1500;
        internal const int EmptyAfterComposeMilliseconds = 12000;
        internal const int StableMilliseconds = 1500;
        internal const int MinimumAfterReleaseMilliseconds = 800;
        internal const int FinishTimeoutMilliseconds = 20000;
        internal const int MaximumHoldMilliseconds = 300000;

        internal static bool ShouldTrigger(bool held, bool alone, bool foregroundIsRemote, bool sameForeground, bool idle)
        { return held && alone && foregroundIsRemote && sameForeground && idle; }

        internal static bool Released(bool held, double sinceUp)
        { return !held && sinceUp >= ReleaseDebounceMilliseconds; }

        // 豆包松开后还会“整理”文字：必须等组字结束、文字稳定，不能按固定秒数收走。
        internal static FinishDecision Decide(bool everComposed, bool composing, string text, bool newText, double sinceRelease, double sinceChange)
        {
            bool hasText = newText && !string.IsNullOrWhiteSpace(text);
            if (sinceRelease >= FinishTimeoutMilliseconds) return hasText || composing ? FinishDecision.Timeout : FinishDecision.Abandon;
            if (composing) return FinishDecision.Wait;
            if (hasText) return sinceRelease >= MinimumAfterReleaseMilliseconds && sinceChange >= StableMilliseconds ? FinishDecision.Deliver : FinishDecision.Wait;
            if (!everComposed && sinceRelease >= AbandonMilliseconds) return FinishDecision.Abandon;
            if (everComposed && sinceRelease >= EmptyAfterComposeMilliseconds) return FinishDecision.Abandon;
            return FinishDecision.Wait;
        }
    }

    internal sealed class HoldSession
    {
        internal HoldPhase Phase { get; private set; }
        internal int Generation { get; private set; }
        internal double StartedAt, ReleasedAt;
        internal int StartingTextVersion;
        internal bool EverComposed;
        internal bool Active { get { return Phase != HoldPhase.Idle && Phase != HoldPhase.Failed; } }
        internal int Begin(double now, int textVersion)
        {
            if (Active) return -1;
            Generation++; Phase = HoldPhase.Listening; EverComposed = false;
            StartedAt = now; ReleasedAt = 0; StartingTextVersion = textVersion; return Generation;
        }
        internal bool Release(double now)
        {
            if (Phase != HoldPhase.Listening) return false;
            ReleasedAt = now; Phase = HoldPhase.Finalizing; return true;
        }
        internal bool TryDeliver(int generation)
        {
            if (generation != Generation || Phase != HoldPhase.Finalizing) return false;
            Phase = HoldPhase.Delivering; return true;
        }
        internal void BeginManualDelivery() { if (Active) throw new InvalidOperationException("请等待当前听写完成。"); Phase = HoldPhase.Delivering; }
        internal void Fail() { Generation++; Phase = HoldPhase.Failed; }
        internal void Reset() { Generation++; Phase = HoldPhase.Idle; }
    }

    internal sealed class HoldSnapshot
    {
        internal bool Held, Alone;
        internal double DownAt, UpAt;
        internal IntPtr DownForeground;
        internal int PressSerial, EscapeSerial;
    }

    internal sealed class OneKeyOptions
    {
        internal int[] PasteKeys;      // 按顺序尝试的粘贴键
        internal bool Learning;        // 还没确定这个远控软件的粘贴键：逐个试，看画面是否变化
        internal string AppName;       // 远控软件进程名，用来记住粘贴键
        internal int PasteDelay;
        internal bool RestoreClipboard;
    }
}
