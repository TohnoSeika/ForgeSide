namespace ForgeSide;

/// <summary>亮色/暗色主题色定义 —— 桃华精心挑选的配色</summary>
public static class ThemeColors
{
    // ── 亮色主题 ──
    public static Color LightBg              = Color.FromArgb(243, 240, 237);
    public static Color LightSurface         = Color.FromArgb(250, 250, 248);
    public static Color LightCard            = Color.FromArgb(255, 255, 255);
    public static Color LightCardHover       = Color.FromArgb(254, 253, 252);
    public static Color LightBorder          = Color.FromArgb(228, 223, 217);
    public static Color LightBorderLight     = Color.FromArgb(238, 235, 230);
    public static Color LightText            = Color.FromArgb(61,  56,  51);
    public static Color LightTextSecondary   = Color.FromArgb(138, 131, 123);
    public static Color LightTextMuted       = Color.FromArgb(181, 175, 168);
    public static Color LightAccent          = Color.FromArgb(224, 160, 184);
    public static Color LightAccentSoft      = Color.FromArgb(246, 232, 239);
    public static Color LightAccentDeep      = Color.FromArgb(208, 128, 152);

    // ── 暗色主题 ──
    public static Color DarkBg              = Color.FromArgb(26,  24,  22);
    public static Color DarkSurface         = Color.FromArgb(31,  29,  26);
    public static Color DarkCard            = Color.FromArgb(38,  36,  33);
    public static Color DarkCardHover       = Color.FromArgb(42,  40,  36);
    public static Color DarkBorder          = Color.FromArgb(56,  52,  48);
    public static Color DarkBorderLight     = Color.FromArgb(48,  45,  41);
    public static Color DarkText            = Color.FromArgb(224, 220, 215);
    public static Color DarkTextSecondary   = Color.FromArgb(158, 151, 144);
    public static Color DarkTextMuted       = Color.FromArgb(107, 101, 96);
    public static Color DarkAccent          = Color.FromArgb(212, 136, 158);
    public static Color DarkAccentSoft      = Color.FromArgb(61,  42,  51);
    public static Color DarkAccentDeep      = Color.FromArgb(184, 112, 128);

    // ── 控制台色 (暗色固定) ──
    public static Color ConsoleBg    = Color.FromArgb(16,  15,  14);
    public static Color ConsoleText  = Color.FromArgb(200, 195, 190);
    public static Color ConsoleDim   = Color.FromArgb(92,  87,  82);
    public static Color ConsoleError = Color.FromArgb(216, 136, 136);
    public static Color ConsoleWarn  = Color.FromArgb(200, 184, 120);
    public static Color ConsoleInfo  = Color.FromArgb(128, 160, 200);

    // ── 当前主题 ————————————————————————
    public static bool IsDark { get; set; } = false;

    // 动态颜色属性
    public static Color Bg           => IsDark ? DarkBg          : LightBg;
    public static Color Surface      => IsDark ? DarkSurface     : LightSurface;
    public static Color Card         => IsDark ? DarkCard        : LightCard;
    public static Color CardHover    => IsDark ? DarkCardHover   : LightCardHover;
    public static Color Border       => IsDark ? DarkBorder      : LightBorder;
    public static Color BorderLight  => IsDark ? DarkBorderLight : LightBorderLight;
    public static Color Text         => IsDark ? DarkText        : LightText;
    public static Color TextSecondary=> IsDark ? DarkTextSecondary: LightTextSecondary;
    public static Color TextMuted    => IsDark ? DarkTextMuted   : LightTextMuted;
    public static Color Accent       => IsDark ? DarkAccent      : LightAccent;
    public static Color AccentSoft   => IsDark ? DarkAccentSoft  : LightAccentSoft;
    public static Color AccentDeep   => IsDark ? DarkAccentDeep  : LightAccentDeep;
}
