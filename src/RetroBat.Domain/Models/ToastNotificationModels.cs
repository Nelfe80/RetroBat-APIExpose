namespace RetroBat.Domain.Models;

public sealed class ToastNotification
{
    /// <summary>Free-form category shown by some skins (info, success, warning...).</summary>
    /// <example>info</example>
    public string Type { get; set; } = string.Empty;
    /// <example>APIExpose</example>
    public string Title { get; set; } = string.Empty;
    /// <example>Hello from Swagger — this toast really shows above EmulationStation.</example>
    public string Message { get; set; } = string.Empty;
    /// <summary>Optional absolute image path displayed in the toast.</summary>
    public string? ImagePath { get; set; }
    /// <example>4000</example>
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
