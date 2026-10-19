namespace LexiLens.Core;

public enum PipelineStage { Session, Capture, Ocr, Stability, Translation, Overlay }
public enum PipelineState { Running, Success, Waiting, Skipped, Warning, Error }

// Metadata only. Never attach screen text, screenshots, request bodies or keys.
public sealed record PipelineEvent(PipelineStage Stage, PipelineState State, string Code, string Message,
    long Milliseconds = 0, int Characters = 0, long Frame = 0, int Area = 0)
{
    public string AreaLabel => Area > 0 ? $"区域 {Area}" : "服务";
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string TimeLabel => Time.ToString("HH:mm:ss.fff");
    public string StageLabel => Stage switch { PipelineStage.Capture => "截图", PipelineStage.Ocr => "文字识别", PipelineStage.Stability => "文字判断", PipelineStage.Translation => "翻译服务", PipelineStage.Overlay => "悬浮显示", _ => "会话" };
    public string StateLabel => State switch { PipelineState.Running => "处理中", PipelineState.Success => "完成", PipelineState.Waiting => "等待", PipelineState.Skipped => "已跳过", PipelineState.Warning => "注意", _ => "失败" };
    public string DurationLabel => Milliseconds > 0 ? Milliseconds + " ms" : "—";
}
