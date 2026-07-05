namespace RetroBat.Domain.Models;

public sealed class ToastNotification
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public int DurationMs { get; set; } = 4000;
    public ToastPosition Position { get; set; } = ToastPosition.BottomRight;
    public ToastAnimation Animation { get; set; } = ToastAnimation.FadeIn;
}

public enum ToastPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum ToastAnimation
{
    None,
    FadeIn,
    SlideFromRight,
    SlideFromLeft,
    SlideFromTop,
    SlideFromBottom
}
