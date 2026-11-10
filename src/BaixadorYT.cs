using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class TimeoutWebClient : WebClient
{
    protected override WebRequest GetWebRequest(Uri address)
    {
        WebRequest request = base.GetWebRequest(address);
        request.Timeout = 10000;
        return request;
    }
}

internal sealed class QueueTextBox : TextBox
{
    private const int WmPaste = 0x0302;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WmPaste || !Multiline) return;

        int caret = SelectionStart;
        if (caret <= 0 || caret > TextLength) return;
        int lineStart = Text.LastIndexOf('\n', Math.Max(0, caret - 1));
        string pastedLine = Text.Substring(lineStart + 1, caret - lineStart - 1).Trim();
        Uri uri;
        bool validYouTubeLink = Uri.TryCreate(pastedLine, UriKind.Absolute, out uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host.Contains(".");
        if (validYouTubeLink && (caret >= TextLength || Text[caret] != '\n'))
        {
            SelectionStart = caret;
            SelectionLength = 0;
            SelectedText = Environment.NewLine;
        }
    }
}

internal sealed class QueueVideoInfo
{
    public string Title;
    public string Details;
    public Image Thumbnail;
    public bool? HasMusicCredit;
    public long EstimatedBytes;
    public double DurationSeconds;
    public List<string> ThumbnailUrls;
    // Formatos reais do vídeo: { altura, tipo (0 = só imagem, 1 = só som, 2 = imagem+som), bytes, kbps }
    public List<long[]> Formats;
    public string InfoJsonPath;
    public DateTime FetchedAt;
}

internal sealed class TrimTargetItem
{
    public string Link;
    public int Number;
    public string Title;
    public int DurationSeconds;
    public Image Thumbnail;
    public override string ToString() { return Title ?? ""; }
}

public sealed class DownloadHistoryEntry
{
    public string FilePath { get; set; }
    public string Format { get; set; }
    public string DownloadedAt { get; set; }
}

internal sealed class QueueVideoRow
{
    public RoundedThumbnail Thumbnail;
    public Label Title;
    public Label Details;
    public Label MusicStatus;
}

// ===================== Motor de animação =====================
// Um único relógio (~60 quadros por segundo) move todas as animações do app. Ele NÃO depende da opção
// "Efeitos de animação" do Windows: as animações funcionam sempre, mesmo com ela desligada.
internal static class Motion
{
    private sealed class Anim
    {
        public object Key;
        public long Start;
        public double Duration;
        public bool Loop;
        public Action<double> Step;
        public Action Done;
        public Func<double, double> Ease;
    }

    private static readonly List<Anim> active = new List<Anim>();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static Timer timer;
    private static readonly Dictionary<Control, object> textKeys = new Dictionary<Control, object>();

    // Mesmas curvas do ForeverNote: cubic-bezier(.22,1,.36,1) (suave) e cubic-bezier(.34,1.45,.64,1) (mola).
    public static double EaseOut(double t) { return 1 - Math.Pow(1 - t, 5); }
    public static double EaseInOut(double t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
    public static double EaseIn(double t) { return t * t * t; }
    public static double Linear(double t) { return t; }
    public static double Spring(double t)
    {
        const double s = 1.70158 * 1.15;
        double u = t - 1;
        return 1 + (s + 1) * u * u * u + s * u * u;
    }

    public static Color Mix(Color from, Color to, double t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * t),
            (int)Math.Round(from.R + (to.R - from.R) * t),
            (int)Math.Round(from.G + (to.G - from.G) * t),
            (int)Math.Round(from.B + (to.B - from.B) * t));
    }

    // Roda uma animação de "ms" milissegundos. Se já existir outra com a mesma chave, ela é substituída.
    public static void Run(object key, int ms, Action<double> step, Action done = null, Func<double, double> ease = null)
    {
        Stop(key);
        Anim anim = new Anim();
        anim.Key = key ?? new object();
        anim.Start = clock.ElapsedMilliseconds;
        anim.Duration = Math.Max(1, ms);
        anim.Step = step;
        anim.Done = done;
        anim.Ease = ease ?? EaseOut;
        active.Add(anim);
        EnsureTimer();
    }

    // Repete para sempre (brilho da barra de progresso, esqueleto de carregamento) até Stop(key).
    public static void Loop(object key, int periodMs, Action<double> step)
    {
        if (IsRunning(key)) return;
        Anim anim = new Anim();
        anim.Key = key;
        anim.Start = clock.ElapsedMilliseconds;
        anim.Duration = Math.Max(1, periodMs);
        anim.Loop = true;
        anim.Step = step;
        anim.Ease = Linear;
        active.Add(anim);
        EnsureTimer();
    }

    public static bool IsRunning(object key)
    {
        foreach (Anim anim in active) if (anim.Key == key) return true;
        return false;
    }

    public static void Stop(object key)
    {
        if (key == null) return;
        active.RemoveAll(delegate(Anim a) { return a.Key == key; });
    }

    private static void EnsureTimer()
    {
        if (timer == null)
        {
            timer = new Timer();
            timer.Interval = 15;
            timer.Tick += delegate { Tick(); };
        }
        if (!timer.Enabled) timer.Start();
    }

    private static void Tick()
    {
        long now = clock.ElapsedMilliseconds;
        Anim[] snapshot = active.ToArray();
        foreach (Anim anim in snapshot)
        {
            if (!active.Contains(anim)) continue;
            double elapsed = now - anim.Start;
            double t = anim.Loop ? (elapsed % anim.Duration) / anim.Duration : Math.Min(1.0, elapsed / anim.Duration);
            bool finished = !anim.Loop && t >= 1.0;
            try { anim.Step(finished ? 1.0 : anim.Ease(t)); }
            catch { active.Remove(anim); continue; }
            if (finished)
            {
                active.Remove(anim);
                if (anim.Done != null)
                {
                    try { anim.Done(); }
                    catch { }
                }
            }
        }
        if (active.Count == 0) timer.Stop();
    }

    // Janela abre com fade e sobe alguns pixels (como os modais do ForeverNote).
    public static void FadeIn(Form form)
    {
        form.Opacity = 0;
        form.Shown += delegate
        {
            int finalTop = form.Top;
            form.Top = finalTop + 14;
            Run(form, 260, delegate(double t)
            {
                if (form.IsDisposed) return;
                form.Opacity = Math.Min(1.0, t * 1.25);
                form.Top = finalTop + (int)Math.Round(14 * (1 - t));
            }, delegate { if (!form.IsDisposed) { form.Opacity = 1; form.Top = finalTop; } }, EaseOut);
        };
    }

    // Texto aparece suavemente (a cor sai do fundo e chega na cor final).
    public static void FadeText(Control control, Color target, int ms = 260)
    {
        Color from = control.BackColor == Color.Transparent && control.Parent != null ? control.Parent.BackColor : control.BackColor;
        object key;
        if (!textKeys.TryGetValue(control, out key))
        {
            key = new object();
            textKeys[control] = key;
            control.Disposed += delegate { textKeys.Remove(control); };
        }
        Run(key, ms, delegate(double t) { if (!control.IsDisposed) control.ForeColor = Mix(from, target, t); },
            delegate { if (!control.IsDisposed) control.ForeColor = target; });
    }
}

// Valor numérico que desliza até o alvo e redesenha o controle a cada quadro.
internal sealed class AnimatedValue
{
    private readonly Control owner;
    private double from;
    private double to;
    public double Value;
    public double Target { get { return to; } }

    public AnimatedValue(Control owner, double initial)
    {
        this.owner = owner;
        Value = initial;
        from = initial;
        to = initial;
    }

    public void AnimateTo(double target, int ms, Func<double, double> ease = null)
    {
        if (Math.Abs(target - to) < 0.0001 && Motion.IsRunning(this)) return;
        if (Math.Abs(target - Value) < 0.0001) { Motion.Stop(this); Value = target; to = target; owner.Invalidate(); return; }
        from = Value;
        to = target;
        Motion.Run(this, ms, delegate(double t)
        {
            Value = from + (to - from) * t;
            if (!owner.IsDisposed) owner.Invalidate();
        }, null, ease);
    }

    public void Set(double value)
    {
        Motion.Stop(this);
        Value = value;
        from = value;
        to = value;
    }
}

// ===================== Paleta (a mesma do ForeverNote) =====================
internal static class Theme
{
    public static bool Dark;
    private static Color Pick(int dark, int light) { return Color.FromArgb(255, Color.FromArgb(Dark ? dark : light)); }

    public static Color Bg { get { return Pick(0x14161a, 0xeceef2); } }
    public static Color Side { get { return Pick(0x17191d, 0xf1f2f5); } }
    public static Color Surface { get { return Pick(0x1e2025, 0xffffff); } }
    public static Color Raised { get { return Pick(0x262930, 0xffffff); } }
    public static Color Sunken { get { return Pick(0x17191d, 0xeef0f3); } }
    public static Color Track { get { return Pick(0x2c2f37, 0xe2e5eb); } }
    public static Color Line { get { return Pick(0x2b2e35, 0xe0e3e9); } }
    public static Color LineStrong { get { return Pick(0x353942, 0xd3d8e0); } }
    public static Color Fg { get { return Pick(0xe7e9ed, 0x1a1d23); } }
    public static Color Muted { get { return Pick(0xa0a6b1, 0x5b6270); } }
    public static Color Faint { get { return Pick(0x6e7480, 0x8d939e); } }
    public static Color Accent { get { return Pick(0x7d9bff, 0x4067e3); } }
    public static Color AccentHi { get { return Pick(0x97afff, 0x3157d4); } }
    public static Color OnAccent { get { return Pick(0x0c1430, 0xffffff); } }
    public static Color Danger { get { return Pick(0xf07a7a, 0xd04a4a); } }
    public static Color Ok { get { return Pick(0x4cc38a, 0x2f9e6a); } }
    public static Color Warn { get { return Pick(0xe6b450, 0xc98a12); } }
    public static Color AccentSoft { get { return Motion.Mix(Surface, Accent, Dark ? 0.16 : 0.11); } }
    public static Color AccentSoftOnBg { get { return Motion.Mix(Bg, Accent, Dark ? 0.16 : 0.11); } }
    public static Color Hover(Color under) { return Motion.Mix(under, Dark ? Color.White : Color.FromArgb(30, 40, 70), Dark ? 0.055 : 0.055); }
    public static Color Press(Color under) { return Motion.Mix(under, Dark ? Color.White : Color.FromArgb(30, 40, 70), 0.09); }

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) { return new Font("Segoe UI", size, style); }
    public static Font Semibold(float size)
    {
        try
        {
            Font font = new Font("Segoe UI Semibold", size);
            if (font.Name == "Segoe UI Semibold") return font;
            font.Dispose();
        }
        catch { }
        return new Font("Segoe UI", size, FontStyle.Bold);
    }
}

internal static class Shapes
{
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        if (r.Width < 1 || r.Height < 1) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    internal static Color ParentBack(Control control)
    {
        Control parent = control.Parent;
        while (parent != null && parent.BackColor.A < 255) parent = parent.Parent;
        return parent == null ? Theme.Bg : parent.BackColor;
    }
}

internal sealed class RoundedPanel : Panel
{
    private readonly int radius;
    private readonly AnimatedValue focus;
    public Color BorderColor { get; set; }
    // Campos de texto/listas: a borda acende na cor de destaque quando o campo está em foco (como no ForeverNote).
    public bool FocusRing { get; set; }
    // Card com uma sombra leve embaixo.
    public bool Elevated { get; set; }

    public RoundedPanel(int cornerRadius)
    {
        radius = cornerRadius;
        BorderColor = Color.Transparent;
        focus = new AnimatedValue(this, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Enter += delegate { if (FocusRing) focus.AnimateTo(1, 200); };
        Leave += delegate { if (FocusRing) focus.AnimateTo(0, 240); };
    }

    internal static GraphicsPath MakePath(Rectangle rectangle, int cornerRadius)
    {
        return Shapes.Round(rectangle, cornerRadius);
    }

    private RectangleF Body
    {
        get { return Elevated ? new RectangleF(0.5f, 0.5f, Width - 2f, Height - 3.5f) : new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Shapes.ParentBack(this));
        Shapes.Smooth(g);
        RectangleF body = Body;
        if (Elevated)
        {
            for (int i = 2; i >= 1; i--)
            {
                RectangleF shadow = new RectangleF(body.X, body.Y + i, body.Width, body.Height);
                using (GraphicsPath path = Shapes.Round(shadow, radius))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(Theme.Dark ? 60 : 12 + 6 * (2 - i), 10, 14, 30))) g.FillPath(brush, path);
            }
        }
        using (GraphicsPath path = Shapes.Round(body, radius))
        using (SolidBrush brush = new SolidBrush(BackColor)) g.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Shapes.Smooth(g);
        RectangleF body = Body;
        double f = focus.Value;
        Color border = f > 0 ? Motion.Mix(BorderColor.A == 0 ? BackColor : BorderColor, Theme.Accent, f) : BorderColor;
        if (border.A > 0)
        {
            using (GraphicsPath path = Shapes.Round(body, radius))
            using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, path);
        }
        if (f > 0.01)
        {
            RectangleF inner = RectangleF.Inflate(body, -1.5f, -1.5f);
            using (GraphicsPath path = Shapes.Round(inner, Math.Max(2, radius - 1)))
            using (Pen pen = new Pen(Color.FromArgb((int)(70 * f), Theme.Accent), 2f)) g.DrawPath(pen, path);
        }
    }
}

internal enum ButtonLook { Normal, Primary, Ghost }

internal sealed class RoundedButton : Control
{
    private readonly int radius;
    private bool isPressed;
    private readonly AnimatedValue hover;
    private readonly AnimatedValue press;
    private readonly AnimatedValue enabledness;
    private ButtonLook look = ButtonLook.Normal;
    public Color BorderColor { get; set; }
    // Cor do texto quando a cor de fundo muda ao passar o mouse (botões fantasmas).
    public Color HoverForeColor { get; set; }
    public ButtonLook Look
    {
        get { return look; }
        set { look = value; Invalidate(); }
    }

    public RoundedButton(int cornerRadius)
    {
        radius = cornerRadius;
        BorderColor = Color.Transparent;
        HoverForeColor = Color.Empty;
        Cursor = Cursors.Hand;
        TabStop = true;
        hover = new AnimatedValue(this, 0);
        press = new AnimatedValue(this, 0);
        enabledness = new AnimatedValue(this, 1);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.StandardClick, false);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Shapes.ParentBack(this));
    }

    private RectangleF Body
    {
        get
        {
            // O botão principal tem um "brilho" colorido embaixo, então o corpo fica um pouco recuado.
            if (look == ButtonLook.Primary) return new RectangleF(3.5f, 1.5f, Width - 8f, Height - 8f);
            return new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        Graphics g = e.Graphics;
        Shapes.Smooth(g);
        Color under = Shapes.ParentBack(this);
        double h = hover.Value;
        double p = press.Value;
        double en = enabledness.Value;
        RectangleF body = Body;
        // Ao clicar, o botão "afunda" levemente (scale .97).
        float shrink = (float)(p * Math.Min(body.Width, body.Height) * 0.03 + p * 0.6);
        body = RectangleF.Inflate(body, -shrink, -shrink * 0.8f);

        Color fill;
        Color text = ForeColor;
        if (look == ButtonLook.Ghost)
        {
            Color baseFill = BackColor.A == 0 ? under : BackColor;
            fill = Motion.Mix(Motion.Mix(under, Theme.Hover(baseFill), h), Theme.Press(baseFill), p);
            if (HoverForeColor != Color.Empty) text = Motion.Mix(ForeColor, HoverForeColor, h);
        }
        else if (look == ButtonLook.Primary)
        {
            fill = Motion.Mix(Motion.Mix(BackColor, Theme.AccentHi, h), Motion.Mix(Theme.AccentHi, Color.Black, 0.08), p);
        }
        else
        {
            fill = Motion.Mix(Motion.Mix(BackColor, Theme.Hover(BackColor), h), Theme.Press(BackColor), p);
        }
        Color disabledFill = Motion.Mix(BackColor.A == 0 ? under : BackColor, under, look == ButtonLook.Primary ? 0.55 : 0.35);
        fill = Motion.Mix(disabledFill, fill, en);
        text = Motion.Mix(Motion.Mix(text, under, 0.6), text, en);

        if (look == ButtonLook.Primary && en > 0.05)
        {
            // Sombra colorida suave (box-shadow: 0 4px 14px -6px accent).
            for (int i = 4; i >= 1; i--)
            {
                RectangleF glow = new RectangleF(body.X + 4 - i, body.Y + 3 + i, body.Width - 8 + 2 * i, body.Height - 2);
                using (GraphicsPath path = Shapes.Round(glow, radius + i))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)((10 + 6 * h) * en), BackColor))) g.FillPath(brush, path);
            }
        }
        if (fill.A > 0)
        {
            using (GraphicsPath path = Shapes.Round(body, radius))
            using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
        }
        Color border = BorderColor;
        if (look == ButtonLook.Normal && border.A > 0) border = Motion.Mix(border, Theme.Faint, h * 0.6);
        if (border.A > 0 && look != ButtonLook.Primary)
        {
            using (GraphicsPath path = Shapes.Round(body, radius))
            using (Pen pen = new Pen(Motion.Mix(Motion.Mix(border, under, 0.5), border, en))) g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            RectangleF ring = RectangleF.Inflate(body, -1.5f, -1.5f);
            using (GraphicsPath path = Shapes.Round(ring, Math.Max(2, radius - 1)))
            using (Pen pen = new Pen(Color.FromArgb(150, Theme.Accent), 2f)) g.DrawPath(pen, path);
        }
        Rectangle textBounds = Rectangle.Round(body);
        TextRenderer.DrawText(g, Text, Font, textBounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { hover.AnimateTo(1, 160); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        isPressed = false;
        hover.AnimateTo(0, 260);
        press.AnimateTo(0, 160);
        base.OnMouseLeave(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled) { isPressed = true; Focus(); Capture = true; press.AnimateTo(1, 90); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool activate = isPressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location) && Enabled;
        isPressed = false;
        Capture = false;
        press.AnimateTo(0, 320, Motion.Spring);
        base.OnMouseUp(e);
        if (activate) OnClick(EventArgs.Empty);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled) { isPressed = true; press.AnimateTo(1, 90); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        bool activate = isPressed && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled;
        isPressed = false;
        press.AnimateTo(0, 320, Motion.Spring);
        base.OnKeyUp(e);
        if (activate) OnClick(EventArgs.Empty);
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (IsHandleCreated && Visible) enabledness.AnimateTo(Enabled ? 1 : 0, 220);
        else { enabledness.Set(Enabled ? 1 : 0); Invalidate(); }
        if (!Enabled) { hover.Set(0); press.Set(0); }
        base.OnEnabledChanged(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

internal sealed class RoundedThumbnail : Control
{
    private Image image;
    private Image previous;
    private readonly AnimatedValue reveal;
    private bool loading;
    private double shimmer;
    private readonly object shimmerKey = new object();
    public Color PlaceholderColor { get; set; }
    public Color PlaceholderTextColor { get; set; }
    public string PlaceholderText { get; set; }
    public float PlaceholderFontSize { get; set; }
    public int Radius { get; set; }

    // Troca de imagem com fade cruzado.
    public Image Image
    {
        get { return image; }
        set
        {
            if (image == value) return;
            previous = image;
            image = value;
            if (value != null && IsHandleCreated && Visible) { reveal.Set(0); reveal.AnimateTo(1, 380); }
            else reveal.Set(1);
            Invalidate();
        }
    }

    // Enquanto carrega, mostra um "esqueleto" com brilho passando.
    public bool Loading
    {
        get { return loading; }
        set
        {
            if (loading == value) return;
            loading = value;
            if (loading)
                Motion.Loop(shimmerKey, 1300, delegate(double t) { shimmer = t; if (!IsDisposed) Invalidate(); else Motion.Stop(shimmerKey); });
            else Motion.Stop(shimmerKey);
            Invalidate();
        }
    }

    public RoundedThumbnail()
    {
        PlaceholderColor = Color.FromArgb(55, 55, 55);
        PlaceholderTextColor = Color.Gainsboro;
        PlaceholderText = "";
        PlaceholderFontSize = 17F;
        Radius = 10;
        reveal = new AnimatedValue(this, 1);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void Dispose(bool disposing)
    {
        Motion.Stop(shimmerKey);
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Shapes.ParentBack(this));
    }

    private void DrawFitted(Graphics g, Image picture, float alpha)
    {
        // Preenche o quadro (corta as bordas) para ficar como uma capa de verdade.
        float scale = Math.Max((float)Width / picture.Width, (float)Height / picture.Height);
        float w = picture.Width * scale;
        float h = picture.Height * scale;
        RectangleF dest = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
        if (alpha >= 0.999f) { g.DrawImage(picture, dest); return; }
        using (System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes())
        {
            System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix();
            matrix.Matrix33 = alpha;
            attributes.SetColorMatrix(matrix);
            g.DrawImage(picture, new PointF[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) },
                new RectangleF(0, 0, picture.Width, picture.Height), GraphicsUnit.Pixel, attributes);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        Graphics g = e.Graphics;
        Shapes.Smooth(g);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        RectangleF bounds = new RectangleF(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Shapes.Round(bounds, Radius))
        {
            g.SetClip(path);
            using (SolidBrush brush = new SolidBrush(PlaceholderColor)) g.FillPath(brush, path);
            float a = (float)Math.Max(0, Math.Min(1, reveal.Value));
            try
            {
                if (previous != null && a < 1) DrawFitted(g, previous, 1 - a);
            }
            catch { previous = null; }
            if (image != null)
            {
                try { DrawFitted(g, image, a); }
                catch { }
            }
            else if (loading)
            {
                float band = Width * 0.6f;
                float x = (float)(-band + (Width + band * 2) * shimmer);
                using (LinearGradientBrush shine = new LinearGradientBrush(new RectangleF(x - 1, 0, band + 2, Height), Color.Transparent, Color.Transparent, 0f))
                {
                    ColorBlend blend = new ColorBlend();
                    Color glow = Color.FromArgb(Theme.Dark ? 28 : 120, Color.White);
                    blend.Colors = new Color[] { Color.FromArgb(0, Color.White), glow, Color.FromArgb(0, Color.White) };
                    blend.Positions = new float[] { 0f, 0.5f, 1f };
                    shine.InterpolationColors = blend;
                    g.FillRectangle(shine, x, 0, band, Height);
                }
            }
            else if (!String.IsNullOrEmpty(PlaceholderText))
            {
                using (Font font = new Font("Segoe UI", PlaceholderFontSize, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(PlaceholderTextColor))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(PlaceholderText, font, textBrush, bounds, format);
            }
            if (a >= 1 && previous != null) previous = null;
        }
        g.ResetClip();
    }
}

internal sealed class PercentBar : Control
{
    private int value;
    private readonly AnimatedValue shown;
    private double sweep;
    private bool active;
    private readonly object sweepKey = new object();
    public Color TrackColor { get; set; }
    public Color FillColor { get; set; }

    // O preenchimento desliza até o valor novo em vez de pular.
    public int Value
    {
        get { return value; }
        set
        {
            int clamped = Math.Max(0, Math.Min(100, value));
            if (clamped == this.value) return;
            bool backwards = clamped < this.value;
            this.value = clamped;
            if (backwards || !IsHandleCreated || !Visible) { shown.Set(clamped); Invalidate(); }
            else shown.AnimateTo(clamped, 420);
        }
    }

    // Brilho passando pela barra enquanto o download está ativo.
    public bool Active
    {
        get { return active; }
        set
        {
            if (active == value) return;
            active = value;
            if (active) Motion.Loop(sweepKey, 1600, delegate(double t) { sweep = t; if (!IsDisposed) Invalidate(); else Motion.Stop(sweepKey); });
            else { Motion.Stop(sweepKey); Invalidate(); }
        }
    }

    public PercentBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TrackColor = Color.FromArgb(226, 232, 240);
        FillColor = Color.FromArgb(64, 103, 227);
        shown = new AnimatedValue(this, 0);
    }

    public double DisplayValue { get { return shown.Value; } }

    protected override void Dispose(bool disposing)
    {
        Motion.Stop(sweepKey);
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Shapes.ParentBack(this));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        Graphics g = e.Graphics;
        Shapes.Smooth(g);
        RectangleF track = new RectangleF(0, 0.5f, Width - 1, Height - 1.5f);
        float r = track.Height / 2f;
        using (GraphicsPath trackPath = Shapes.Round(track, r))
        using (SolidBrush trackBrush = new SolidBrush(TrackColor)) g.FillPath(trackBrush, trackPath);
        float fillWidth = (float)((Width - 1) * shown.Value / 100.0);
        if (fillWidth > 0.5f)
        {
            RectangleF fill = new RectangleF(0, 0.5f, Math.Max(track.Height, fillWidth), track.Height);
            using (GraphicsPath fillPath = Shapes.Round(fill, r))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(fill.X - 1, 0, fill.Width + 2, Height),
                    Motion.Mix(FillColor, Color.White, Theme.Dark ? 0.0 : 0.12), Motion.Mix(FillColor, Theme.Dark ? Color.White : Color.Black, Theme.Dark ? 0.12 : 0.06), 0f))
                    g.FillPath(brush, fillPath);
                if (active)
                {
                    g.SetClip(fillPath);
                    float band = Math.Max(60f, Width * 0.18f);
                    float x = (float)(-band + (fill.Width + band) * sweep);
                    using (LinearGradientBrush shine = new LinearGradientBrush(new RectangleF(x - 1, 0, band + 2, Height), Color.Transparent, Color.Transparent, 0f))
                    {
                        ColorBlend blend = new ColorBlend();
                        blend.Colors = new Color[] { Color.FromArgb(0, Color.White), Color.FromArgb(95, Color.White), Color.FromArgb(0, Color.White) };
                        blend.Positions = new float[] { 0f, 0.5f, 1f };
                        shine.InterpolationColors = blend;
                        g.FillRectangle(shine, x, 0, band, Height);
                    }
                    g.ResetClip();
                }
            }
        }
    }
}

// Interruptor (liga/desliga) no lugar das caixinhas de marcar. Continua sendo um CheckBox por dentro.
internal sealed class ToggleSwitch : CheckBox
{
    private readonly AnimatedValue knob;
    private readonly AnimatedValue hover;
    private const int TrackWidth = 34;
    private const int TrackHeight = 20;

    public ToggleSwitch()
    {
        knob = new AnimatedValue(this, 0);
        hover = new AnimatedValue(this, 0);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(TrackWidth + 8 + text.Width + 2, Math.Max(TrackHeight + 4, text.Height + 4));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        if (IsHandleCreated && Visible) knob.AnimateTo(Checked ? 1 : 0, 340, Motion.Spring);
        else { knob.Set(Checked ? 1 : 0); Invalidate(); }
        base.OnCheckedChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e) { hover.AnimateTo(1, 160); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.AnimateTo(0, 240); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color under = Shapes.ParentBack(this);
        g.Clear(under);
        Shapes.Smooth(g);
        double k = knob.Value;
        double kc = Math.Max(0, Math.Min(1, k));
        float top = (Height - TrackHeight) / 2f;
        RectangleF track = new RectangleF(1, top, TrackWidth, TrackHeight);
        Color off = Motion.Mix(Theme.LineStrong, Theme.Faint, hover.Value * 0.5);
        Color on = Motion.Mix(Theme.Accent, Theme.AccentHi, hover.Value);
        Color trackColor = Motion.Mix(off, on, kc);
        if (!Enabled) trackColor = Motion.Mix(trackColor, under, 0.5);
        using (GraphicsPath path = Shapes.Round(track, TrackHeight / 2f))
        using (SolidBrush brush = new SolidBrush(trackColor)) g.FillPath(brush, path);
        float d = TrackHeight - 6;
        float x = (float)(track.X + 3 + (TrackWidth - 6 - d) * k);
        // A bolinha "estica" um pouco enquanto anda.
        float stretch = (float)(Math.Sin(Math.PI * kc) * 4 * (Motion.IsRunning(knob) ? 1 : 0));
        RectangleF dot = new RectangleF(x - stretch / 2f, top + 3, d + stretch, d);
        using (GraphicsPath shadowPath = Shapes.Round(new RectangleF(dot.X, dot.Y + 1, dot.Width, dot.Height), d / 2f))
        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillPath(shadow, shadowPath);
        using (GraphicsPath dotPath = Shapes.Round(dot, d / 2f))
        using (SolidBrush brush = new SolidBrush(Color.White)) g.FillPath(brush, dotPath);
        Color text = Enabled ? ForeColor : Motion.Mix(ForeColor, under, 0.55);
        Rectangle textRect = new Rectangle(TrackWidth + 9, 0, Width - TrackWidth - 9, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using (GraphicsPath ring = Shapes.Round(RectangleF.Inflate(track, 1.5f, 1.5f), TrackHeight / 2f + 1.5f))
            using (Pen pen = new Pen(Color.FromArgb(120, Theme.Accent), 2f)) g.DrawPath(pen, ring);
        }
    }
}

// Seletor em "pílulas" (Áudio | Vídeo | Vídeo + Áudio). A pílula desliza com efeito de mola.
internal sealed class SegmentedControl : Control
{
    private string[] items = new string[0];
    private int selectedIndex = -1;
    private int hoverIndex = -1;
    private readonly AnimatedValue pillX;
    private readonly AnimatedValue pillW;
    private readonly AnimatedValue appear;
    public event EventHandler SelectedIndexChanged;
    public Color TrackColor { get; set; }
    public Color PillColor { get; set; }
    public Color SelectedTextColor { get; set; }
    public Color TextColor { get; set; }
    public bool AccentPill { get; set; }
    public int Radius { get; set; }

    public SegmentedControl()
    {
        pillX = new AnimatedValue(this, 0);
        pillW = new AnimatedValue(this, 0);
        appear = new AnimatedValue(this, 0);
        Radius = 10;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public string[] Items
    {
        get { return items; }
        set
        {
            items = value ?? new string[0];
            if (selectedIndex >= items.Length) selectedIndex = items.Length - 1;
            SnapPill();
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set
        {
            if (value < -1 || value >= items.Length || value == selectedIndex) return;
            selectedIndex = value;
            RectangleF r = SegmentRect(value);
            if (IsHandleCreated && Visible && appear.Value > 0.5)
            {
                pillX.AnimateTo(r.X, 380, Motion.Spring);
                pillW.AnimateTo(r.Width, 380, Motion.Spring);
            }
            else SnapPill();
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
    }

    public string SelectedItem { get { return selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : null; } }

    private void SnapPill()
    {
        if (selectedIndex < 0) return;
        RectangleF r = SegmentRect(selectedIndex);
        pillX.Set(r.X);
        pillW.Set(r.Width);
        appear.Set(1);
    }

    protected override void OnResize(EventArgs e) { SnapPill(); base.OnResize(e); }
    protected override void OnFontChanged(EventArgs e) { SnapPill(); base.OnFontChanged(e); }

    // Larguras proporcionais ao texto, preenchendo toda a largura do controle.
    private float[] Widths()
    {
        float[] widths = new float[items.Length];
        if (items.Length == 0) return widths;
        float total = 0;
        for (int i = 0; i < items.Length; i++)
        {
            widths[i] = TextRenderer.MeasureText(items[i], Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            total += widths[i];
        }
        float inner = Width - 6;
        float spare = Math.Max(0, inner - total) / items.Length;
        for (int i = 0; i < items.Length; i++) widths[i] += spare;
        float sum = 0;
        foreach (float w in widths) sum += w;
        if (sum > inner && sum > 0) for (int i = 0; i < items.Length; i++) widths[i] *= inner / sum;
        return widths;
    }

    private RectangleF SegmentRect(int index)
    {
        float[] widths = Widths();
        float x = 3;
        for (int i = 0; i < index && i < widths.Length; i++) x += widths[i];
        return new RectangleF(x, 3, index < widths.Length ? widths[index] : 0, Height - 7);
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < items.Length; i++)
            if (SegmentRect(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != hoverIndex) { hoverIndex = hit; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            int hit = HitTest(e.Location);
            if (hit >= 0) SelectedIndex = hit;
        }
        base.OnMouseDown(e);
    }
    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left && selectedIndex > 0) SelectedIndex = selectedIndex - 1;
        else if (e.KeyCode == Keys.Right && selectedIndex < items.Length - 1) SelectedIndex = selectedIndex + 1;
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color under = Shapes.ParentBack(this);
        g.Clear(under);
        Shapes.Smooth(g);
        RectangleF outer = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
        using (GraphicsPath path = Shapes.Round(outer, Radius))
        using (SolidBrush brush = new SolidBrush(TrackColor)) g.FillPath(brush, path);
        for (int i = 0; i < items.Length; i++)
        {
            if (i != hoverIndex || i == selectedIndex || !Enabled) continue;
            using (GraphicsPath path = Shapes.Round(SegmentRect(i), Radius - 3))
            using (SolidBrush brush = new SolidBrush(Theme.Hover(TrackColor))) g.FillPath(brush, path);
        }
        if (selectedIndex >= 0)
        {
            RectangleF pill = new RectangleF((float)pillX.Value, 3, (float)Math.Max(4, pillW.Value), Height - 7);
            Color pillColor = AccentPill ? Theme.Accent : PillColor;
            if (!Enabled) pillColor = Motion.Mix(pillColor, TrackColor, 0.5);
            using (GraphicsPath shadowPath = Shapes.Round(new RectangleF(pill.X, pill.Y + 1, pill.Width, pill.Height), Radius - 3))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 22, 10, 14, 30))) g.FillPath(shadow, shadowPath);
            using (GraphicsPath path = Shapes.Round(pill, Radius - 3))
            using (SolidBrush brush = new SolidBrush(pillColor)) g.FillPath(brush, path);
            if (Focused && ShowFocusCues)
                using (GraphicsPath path = Shapes.Round(RectangleF.Inflate(pill, -1, -1), Radius - 4))
                using (Pen pen = new Pen(Color.FromArgb(150, Theme.Accent), 1.5f)) g.DrawPath(pen, path);
        }
        for (int i = 0; i < items.Length; i++)
        {
            RectangleF r = SegmentRect(i);
            // A cor do texto acompanha a pílula enquanto ela passa por baixo.
            double cover = 0;
            if (selectedIndex >= 0)
            {
                double left = Math.Max(r.X, pillX.Value);
                double right = Math.Min(r.Right, pillX.Value + pillW.Value);
                cover = Math.Max(0, Math.Min(1, (right - left) / Math.Max(1, r.Width)));
            }
            Color selectedText = AccentPill ? Theme.OnAccent : SelectedTextColor;
            Color color = Motion.Mix(TextColor, selectedText, cover);
            if (!Enabled) color = Motion.Mix(color, under, 0.5);
            TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(r), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}

// Marca do app: quadrado arredondado com degradê e uma seta de download.
internal sealed class BrandMark : Control
{
    private readonly AnimatedValue bounce;
    public BrandMark()
    {
        bounce = new AnimatedValue(this, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // A seta dá um pulinho (ex.: quando um download termina).
    public void Bounce()
    {
        bounce.Set(0);
        Motion.Run(this, 700, delegate(double t) { bounce.Value = Math.Sin(t * Math.PI * 2) * (1 - t); if (!IsDisposed) Invalidate(); }, null, Motion.Linear);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Shapes.ParentBack(this));
        Shapes.Smooth(g);
        RectangleF box = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
        using (GraphicsPath path = Shapes.Round(box, Width * 0.28f))
        using (LinearGradientBrush brush = new LinearGradientBrush(box, Motion.Mix(Theme.Accent, Color.White, 0.18), Motion.Mix(Theme.Accent, Color.FromArgb(120, 70, 230), 0.45), 45f))
            g.FillPath(brush, path);
        float s = Width / 32f;
        float dy = (float)(bounce.Value * 3 * s);
        Color ink = Theme.Dark ? Theme.OnAccent : Color.White;
        using (Pen pen = new Pen(ink, 2.6f * s))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLine(pen, 16 * s, 8 * s + dy, 16 * s, 18.5f * s + dy);
            g.DrawLines(pen, new PointF[] { new PointF(11 * s, 14 * s + dy), new PointF(16 * s, 19 * s + dy), new PointF(21 * s, 14 * s + dy) });
            g.DrawLine(pen, 10 * s, 23.5f * s, 22 * s, 23.5f * s);
        }
    }
}

// Listas suspensas desenhadas com as cores do tema (a lista aberta também fica escura no modo escuro).
internal static class ComboStyle
{
    private static readonly HashSet<ComboBox> styled = new HashSet<ComboBox>();

    public static void Apply(ComboBox box)
    {
        if (styled.Contains(box) || box.DrawMode != DrawMode.Normal) return;
        styled.Add(box);
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 20;
        box.DrawItem += delegate(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
            Color back = box.BackColor;
            Color fore = box.Enabled ? box.ForeColor : Motion.Mix(box.ForeColor, back, 0.5);
            using (SolidBrush brush = new SolidBrush(selected ? Theme.AccentSoft : back)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (selected)
            {
                using (SolidBrush bar = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
                fore = Theme.Dark ? Theme.Fg : Theme.Accent;
            }
            Rectangle text = new Rectangle(e.Bounds.X + (edit ? 0 : 10), e.Bounds.Y, e.Bounds.Width - (edit ? 0 : 10), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, box.GetItemText(box.Items[e.Index]), box.Font, text, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        box.DropDownClosed += delegate { box.Invalidate(); };
    }
}

// Pinta um formulário inteiro de uma vez a cada quadro (sem "piscar" ao mover vários controles).
internal static class Redraw
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, int flags);

    private static readonly bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;

    public static void Suspend(Control control)
    {
        if (!windows || !control.IsHandleCreated) return;
        try { SendMessage(control.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    public static void Resume(Control control)
    {
        if (!control.IsHandleCreated) return;
        if (!windows) { control.Invalidate(true); return; }
        try
        {
            SendMessage(control.Handle, 0x000B, new IntPtr(1), IntPtr.Zero);
            // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        }
        catch { control.Invalidate(true); }
    }
}

// Área tracejada "arraste aqui", que acende quando um arquivo é arrastado por cima.
internal sealed class DropZone : Control
{
    private readonly AnimatedValue glow;
    public string Glyph { get; set; }
    public string Title { get; set; }
    public string Hint { get; set; }

    public DropZone()
    {
        glow = new AnimatedValue(this, 0);
        Glyph = "⇪";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public bool Highlighted
    {
        set { glow.AnimateTo(value ? 1 : 0, value ? 180 : 260); }
    }

    protected override void OnMouseEnter(EventArgs e) { glow.AnimateTo(0.45, 180); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { glow.AnimateTo(0, 260); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color under = Shapes.ParentBack(this);
        g.Clear(under);
        Shapes.Smooth(g);
        double k = glow.Value;
        RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
        using (GraphicsPath path = Shapes.Round(box, 14))
        {
            using (SolidBrush fill = new SolidBrush(Motion.Mix(Theme.Surface, Theme.AccentSoft, k))) g.FillPath(fill, path);
            using (Pen pen = new Pen(Motion.Mix(Theme.LineStrong, Theme.Accent, k), 1.5f))
            {
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new float[] { 4f, 3f };
                g.DrawPath(pen, path);
            }
        }
        float lift = (float)(k * 4);
        float circle = 44;
        RectangleF disc = new RectangleF((Width - circle) / 2f, Height / 2f - 46 - lift, circle, circle);
        using (SolidBrush brush = new SolidBrush(Motion.Mix(Theme.AccentSoft, Theme.Accent, k * 0.25))) g.FillEllipse(brush, disc);
        TextRenderer.DrawText(g, Glyph, new Font("Segoe UI", 15F), Rectangle.Round(disc), Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (Font titleFont = Theme.Semibold(10.5F))
            TextRenderer.DrawText(g, Title ?? "", titleFont, new Rectangle(0, (int)(Height / 2f + 6), Width, 22), Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Hint ?? "", Font, new Rectangle(0, (int)(Height / 2f + 30), Width, 20), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
    }
}

internal static class Buffering
{
    public static void Enable(Control control)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(control, true, null);
        }
        catch { }
    }
}

internal sealed class TimeRangeSlider : Control
{
    private static readonly Font StatusFont = new Font("Segoe UI", 8F);
    private static readonly Font ZoomFont = new Font("Segoe UI", 7.5F);
    private const int EdgeMargin = 12;
    private const int HandleHitZone = 10;
    private const int FadeZone = 18;
    private int minimum = 0;
    private int maximum = 100;
    private int startValue = 0;
    private int endValue = 100;
    private int dragging = 0;      // 1 início, 2 fim, 3 fade in, 4 fade out
    private int pressX = -1;
    private float[] peaks;
    private string statusText = "";
    private double playheadSeconds = -1;
    private double fadeIn;
    private double fadeOut;
    private double viewStart = 0;
    private double viewEnd = 100;

    public Color TrackColor { get; set; }
    public Color HighlightColor { get; set; }
    public Color WaveBackColor { get; set; }
    public Color StatusTextColor { get; set; }
    public Color PlayheadColor { get; set; }
    public event EventHandler ValueChanged;
    // Clique simples na onda (fora das alças): pede para ouvir a partir daquele segundo.
    public event Action<int> SeekRequested;
    // Setas ← →: pedem para mover a agulha de reprodução (em segundos, + ou -).
    public event Action<int> PlayheadNudge;
    // Teclas I / O: marcar início (true) ou fim (false) do trecho onde está a agulha.
    public event Action<bool> MarkRequested;
    // Arrastar na onda (fora das alças) leva a agulha junto; ao soltar, avisa a posição final.
    public event Action<double> ScrubFinished;
    private bool scrubbing;
    public bool IsScrubbing { get { return scrubbing; } }

    public float[] Peaks
    {
        get { return peaks; }
        set { peaks = value; Invalidate(); }
    }

    public string StatusText
    {
        get { return statusText; }
        set { statusText = value ?? ""; Invalidate(); }
    }

    public double PlayheadSeconds
    {
        get { return playheadSeconds; }
        set { if (Math.Abs(playheadSeconds - value) > 0.001) { playheadSeconds = value; Invalidate(); } }
    }

    // Fades em segundos (passos de 0,1 s). Os setters não disparam ValueChanged.
    public double FadeIn
    {
        get { return fadeIn; }
        set { fadeIn = ClampFade(value, fadeOut); Invalidate(); }
    }

    public double FadeOut
    {
        get { return fadeOut; }
        set { fadeOut = ClampFade(value, fadeIn); Invalidate(); }
    }

    public int Minimum
    {
        get { return minimum; }
        set { minimum = Math.Max(0, value); ResetView(); }
    }

    public int Maximum
    {
        get { return maximum; }
        set
        {
            int newMaximum = Math.Max(minimum + 1, value);
            bool changed = newMaximum != maximum;
            maximum = newMaximum;
            if (endValue > maximum || endValue == 0) endValue = maximum;
            if (changed) ResetView();
            else ClampView();
            Invalidate();
        }
    }

    public int StartValue
    {
        get { return startValue; }
        set
        {
            int val = Math.Max(minimum, Math.Min(value, endValue));
            if (val != startValue)
            {
                startValue = val;
                ClampFadesToRange();
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
    }

    public int EndValue
    {
        get { return endValue; }
        set
        {
            int val = Math.Min(maximum, Math.Max(value, startValue));
            if (val != endValue)
            {
                endValue = val;
                ClampFadesToRange();
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
    }

    public bool IsZoomed { get { return (viewEnd - viewStart) < (maximum - minimum) - 0.01; } }

    // Trechos já guardados deste vídeo, desenhados como faixas discretas na base da onda.
    private List<int[]> marks = new List<int[]>();
    public List<int[]> Marks
    {
        get { return marks; }
        set { marks = value ?? new List<int[]>(); Invalidate(); }
    }

    public TimeRangeSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        HighlightColor = Theme.Accent;
        TrackColor = Color.FromArgb(203, 213, 225);
        WaveBackColor = Color.FromArgb(248, 250, 252);
        StatusTextColor = Color.FromArgb(100, 116, 139);
        PlayheadColor = Color.FromArgb(15, 23, 42);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    // ----- fades -----

    private double ClampFade(double value, double other)
    {
        double room = Math.Max(0, (endValue - startValue) - other);
        double v = Math.Max(0, Math.Min(value, room));
        return Math.Round(v * 10) / 10.0;
    }

    private void ClampFadesToRange()
    {
        double length = Math.Max(0, endValue - startValue);
        if (fadeIn + fadeOut > length)
        {
            double scale = length / Math.Max(0.001, fadeIn + fadeOut);
            fadeIn = Math.Round(fadeIn * scale * 10) / 10.0;
            fadeOut = Math.Round(fadeOut * scale * 10) / 10.0;
        }
    }

    // ----- zoom -----

    public void ResetView()
    {
        viewStart = minimum;
        viewEnd = maximum;
        Invalidate();
    }

    private void ClampView()
    {
        double full = maximum - minimum;
        double span = Math.Min(full, Math.Max(0.5, viewEnd - viewStart));
        if (viewStart < minimum) viewStart = minimum;
        viewEnd = viewStart + span;
        if (viewEnd > maximum)
        {
            viewEnd = maximum;
            viewStart = maximum - span;
        }
    }

    private void Zoom(double anchor, double factor)
    {
        double full = maximum - minimum;
        double span = viewEnd - viewStart;
        double minSpan = Math.Min(full, 5.0);
        double newSpan = Math.Max(minSpan, Math.Min(full, span * factor));
        double ratio = span <= 0 ? 0.5 : (anchor - viewStart) / span;
        viewStart = anchor - ratio * newSpan;
        viewEnd = viewStart + newSpan;
        ClampView();
        Invalidate();
    }

    private void Pan(double seconds)
    {
        viewStart += seconds;
        viewEnd += seconds;
        ClampView();
        Invalidate();
    }

    private void EnsureVisible(double seconds)
    {
        double span = viewEnd - viewStart;
        if (seconds < viewStart) Pan(seconds - viewStart - span * 0.1);
        else if (seconds > viewEnd) Pan(seconds - viewEnd + span * 0.1);
    }

    // ----- coordenadas -----

    private int UsableWidth { get { return Width - EdgeMargin * 2; } }

    private double ValueToXd(double val)
    {
        double span = viewEnd - viewStart;
        if (UsableWidth <= 0 || span <= 0) return EdgeMargin;
        return EdgeMargin + (val - viewStart) / span * UsableWidth;
    }

    private int ValueToX(double val)
    {
        double x = ValueToXd(val);
        return (int)Math.Round(Math.Max(-10000, Math.Min(10000, x)));
    }

    private double XToTime(int x)
    {
        int usableWidth = UsableWidth;
        if (usableWidth <= 0) return minimum;
        int clampedX = Math.Max(EdgeMargin, Math.Min(x, Width - EdgeMargin));
        double t = viewStart + (double)(clampedX - EdgeMargin) / usableWidth * (viewEnd - viewStart);
        return Math.Max(minimum, Math.Min(maximum, t));
    }

    private int XToValue(int x)
    {
        return (int)Math.Round(XToTime(x));
    }

    private bool IsVisibleX(int x)
    {
        return x >= EdgeMargin - 1 && x <= Width - EdgeMargin + 1;
    }

    private int HitTest(int x, int y)
    {
        if (y <= FadeZone && endValue > startValue)
        {
            int fadeInX = ValueToX(startValue + fadeIn);
            int fadeOutX = ValueToX(endValue - fadeOut);
            int distIn = IsVisibleX(fadeInX) ? Math.Abs(x - fadeInX) : 9999;
            int distOut = IsVisibleX(fadeOutX) ? Math.Abs(x - fadeOutX) : 9999;
            if (distIn <= 7 || distOut <= 7) return distIn <= distOut ? 3 : 4;
        }
        int startX = ValueToX(startValue);
        int distStart = IsVisibleX(startX) ? Math.Abs(x - startX) : 9999;
        int endXv = ValueToX(endValue);
        int distEnd = IsVisibleX(endXv) ? Math.Abs(x - endXv) : 9999;
        if (distStart > HandleHitZone && distEnd > HandleHitZone) return 0;
        if (distStart == distEnd)
        {
            // Alças sobrepostas: escolhe a que consegue se mover.
            if (startValue >= maximum) return 1;
            if (endValue <= minimum) return 2;
            return x < startX ? 1 : 2;
        }
        return distStart < distEnd ? 1 : 2;
    }

    // ----- mouse -----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (CanFocus) Focus();
        dragging = HitTest(e.X, e.Y);
        pressX = e.X;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging == 1)
        {
            StartValue = Math.Min(XToValue(e.X), endValue);
        }
        else if (dragging == 2)
        {
            EndValue = Math.Max(XToValue(e.X), startValue);
        }
        else if (dragging == 3 || dragging == 4)
        {
            double value = dragging == 3 ? XToTime(e.X) - startValue : endValue - XToTime(e.X);
            double before = dragging == 3 ? fadeIn : fadeOut;
            if (dragging == 3) fadeIn = ClampFade(value, fadeOut);
            else fadeOut = ClampFade(value, fadeIn);
            double after = dragging == 3 ? fadeIn : fadeOut;
            Invalidate();
            if (Math.Abs(before - after) > 0.001 && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
        else if (pressX >= 0 && e.Button == MouseButtons.Left && (scrubbing || Math.Abs(e.X - pressX) > 4))
        {
            scrubbing = true;
            playheadSeconds = XToTime(e.X);
            if (IsZoomed && (e.X < EdgeMargin + 8 || e.X > Width - EdgeMargin - 8)) EnsureVisible(playheadSeconds);
            Invalidate();
        }
        else if (e.Button == MouseButtons.None)
        {
            Cursor wanted = HitTest(e.X, e.Y) != 0 ? Cursors.SizeWE : Cursors.Hand;
            if (Cursor != wanted) Cursor = wanted;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasScrub = scrubbing;
        bool wasClick = !wasScrub && dragging == 0 && pressX >= 0 && e.Button == MouseButtons.Left && Math.Abs(e.X - pressX) <= 4;
        scrubbing = false;
        dragging = 0;
        pressX = -1;
        Invalidate();
        if (wasScrub && Enabled && ScrubFinished != null) ScrubFinished(XToTime(e.X));
        else if (wasClick && Enabled && SeekRequested != null) SeekRequested(XToValue(e.X));
    }

    // Ctrl + rodinha: zoom no ponto do mouse. Rodinha sozinha (com zoom): anda pela onda.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || maximum - minimum <= 0) return;
        HandledMouseEventArgs handled = e as HandledMouseEventArgs;
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            Zoom(XToTime(e.X), e.Delta > 0 ? 0.75 : 1.0 / 0.75);
            if (handled != null) handled.Handled = true;
        }
        else if (IsZoomed)
        {
            Pan(-(e.Delta / 120.0) * (viewEnd - viewStart) * 0.15);
            if (handled != null) handled.Handled = true;
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        dragging = 0;
        pressX = -1;
        scrubbing = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    // ----- teclado: ←/→ move a alça ativa 1 s (Shift = 10 s); ↑/↓ troca a alça -----

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Left || key == Keys.Right) return true;
        return base.IsInputKey(keyData);
    }

    // ← → movem a agulha 1 s (Shift = 5 s). As alças se ajustam arrastando ou pelas caixinhas de tempo.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        if ((e.KeyCode == Keys.I || e.KeyCode == Keys.O) && !e.Control && !e.Alt)
        {
            if (MarkRequested != null) MarkRequested(e.KeyCode == Keys.I);
            e.Handled = true;
            return;
        }
        if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right) return;
        int step = e.Shift ? 5 : 1;
        int delta = e.KeyCode == Keys.Left ? -step : step;
        if (PlayheadNudge != null) PlayheadNudge(delta);
        if (playheadSeconds >= 0) EnsureVisible(Math.Max(minimum, Math.Min(maximum, playheadSeconds)));
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // ----- desenho -----

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
    }

    private double FadeGain(double t)
    {
        double gain = 1.0;
        if (fadeIn > 0 && t < startValue + fadeIn) gain = Math.Max(0, (t - startValue) / fadeIn);
        if (fadeOut > 0 && t > endValue - fadeOut) gain = Math.Min(gain, Math.Max(0, (endValue - t) / fadeOut));
        return gain;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 30 || Height < 10) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath boxPath = RoundedPanel.MakePath(box, 8))
        using (SolidBrush boxBrush = new SolidBrush(WaveBackColor))
            g.FillPath(boxBrush, boxPath);

        int usableWidth = UsableWidth;
        if (usableWidth <= 0) return;
        int left = EdgeMargin;
        int right = Width - EdgeMargin;
        int mid = Height / 2;
        int startX = ValueToX(startValue);
        int endX = ValueToX(endValue);
        int selLeft = Math.Max(left, startX);
        int selRight = Math.Min(right, endX);

        if (Enabled && selRight > selLeft)
        {
            using (SolidBrush tint = new SolidBrush(Color.FromArgb(34, HighlightColor)))
                g.FillRectangle(tint, selLeft, 4, selRight - selLeft, Height - 8);
        }

        bool hasWave = Enabled && peaks != null && peaks.Length > 0;
        double full = Math.Max(0.001, maximum - minimum);
        double span = Math.Max(0.001, viewEnd - viewStart);
        if (hasWave)
        {
            // Barras finas: verdes dentro do trecho (mais baixas onde há fade), neutras fora dele.
            g.SmoothingMode = SmoothingMode.None;
            const int barWidth = 2;
            const int step = 3;
            int count = Math.Max(1, usableWidth / step);
            int maxHeight = Height - 14;
            using (SolidBrush inBrush = new SolidBrush(HighlightColor))
            using (SolidBrush outBrush = new SolidBrush(TrackColor))
            {
                for (int i = 0; i < count; i++)
                {
                    double t0 = viewStart + span * i / count;
                    double t1 = viewStart + span * (i + 1) / count;
                    int from = (int)((t0 - minimum) / full * peaks.Length);
                    int to = (int)Math.Ceiling((t1 - minimum) / full * peaks.Length);
                    from = Math.Max(0, Math.Min(peaks.Length - 1, from));
                    to = Math.Max(from + 1, Math.Min(peaks.Length, to));
                    float peak = 0f;
                    for (int k = from; k < to; k++) if (peaks[k] > peak) peak = peaks[k];
                    int x = left + i * step;
                    bool inside = x + barWidth > startX && x < endX;
                    double gain = inside ? FadeGain((t0 + t1) / 2) : 1.0;
                    int h = Math.Max(2, (int)(peak * maxHeight * gain));
                    g.FillRectangle(inside ? inBrush : outBrush, x, mid - h / 2, barWidth, h);
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }
        else
        {
            int trackY = mid - 2;
            using (GraphicsPath trackPath = RoundedPanel.MakePath(new Rectangle(left, trackY, usableWidth, 4), 2))
            using (SolidBrush trackBrush = new SolidBrush(TrackColor))
                g.FillPath(trackBrush, trackPath);
            if (Enabled && selRight > selLeft)
            {
                using (GraphicsPath hlPath = RoundedPanel.MakePath(new Rectangle(selLeft, trackY, Math.Max(2, selRight - selLeft), 4), 2))
                using (SolidBrush hlBrush = new SolidBrush(HighlightColor))
                    g.FillPath(hlBrush, hlPath);
            }
            if (statusText.Length > 0)
            {
                TextRenderer.DrawText(g, statusText, StatusFont, new Rectangle(0, 3, Width, mid - 6), StatusTextColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        if (!Enabled) return;

        // Linhas de fade (rampa, como no Premiere) e as alcinhas quadradas no topo.
        g.SetClip(new Rectangle(left - 6, 0, usableWidth + 12, Height));
        if (marks.Count > 0)
        {
            using (SolidBrush markBrush = new SolidBrush(Color.FromArgb(150, HighlightColor)))
            {
                foreach (int[] mark in marks)
                {
                    int markLeft = Math.Max(left, ValueToX(mark[0]));
                    int markRight = Math.Min(right, ValueToX(mark[1]));
                    if (markRight > markLeft) g.FillRectangle(markBrush, markLeft, Height - 9, markRight - markLeft, 3);
                }
            }
        }
        int fadeInX = ValueToX(startValue + fadeIn);
        int fadeOutX = ValueToX(endValue - fadeOut);
        using (Pen rampPen = new Pen(Color.FromArgb(220, HighlightColor), 1.6f))
        {
            if (fadeIn > 0) g.DrawLine(rampPen, startX, Height - 6, fadeInX, 7);
            if (fadeOut > 0) g.DrawLine(rampPen, fadeOutX, 7, endX, Height - 6);
        }
        if (endValue > startValue)
        {
            DrawFadeHandle(g, fadeInX, dragging == 3);
            DrawFadeHandle(g, fadeOutX, dragging == 4);
        }

        if (playheadSeconds >= 0)
        {
            int px = ValueToX(playheadSeconds);
            if (IsVisibleX(px))
            {
                using (Pen playPen = new Pen(PlayheadColor, 2f))
                    g.DrawLine(playPen, px, 5, px, Height - 6);
                using (SolidBrush dot = new SolidBrush(PlayheadColor))
                    g.FillEllipse(dot, px - 4, 2, 8, 8);
            }
        }

        if (IsVisibleX(startX)) DrawHandle(g, startX, dragging == 1, false);
        if (IsVisibleX(endX)) DrawHandle(g, endX, dragging == 2, false);
        g.ResetClip();

        if (IsZoomed)
        {
            // Indicador de zoom e mini barra mostrando a parte visível.
            double factor = full / span;
            TextRenderer.DrawText(g, "Zoom " + factor.ToString("0.#", CultureInfo.CurrentCulture) + "×", ZoomFont,
                    new Rectangle(Width - 84, 2, 76, 14), StatusTextColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int barX = left + (int)((viewStart - minimum) / full * usableWidth);
            int barW = Math.Max(8, (int)(span / full * usableWidth));
            using (SolidBrush track = new SolidBrush(TrackColor))
                g.FillRectangle(track, left, Height - 4, usableWidth, 2);
            using (SolidBrush thumb = new SolidBrush(HighlightColor))
                g.FillRectangle(thumb, barX, Height - 4, barW, 2);
        }
    }

    private void DrawFadeHandle(Graphics g, int x, bool active)
    {
        if (!IsVisibleX(x)) return;
        Rectangle square = new Rectangle(x - 4, 3, 8, 8);
        using (GraphicsPath path = RoundedPanel.MakePath(square, 2))
        using (SolidBrush fill = new SolidBrush(active ? HighlightColor : Color.White))
        using (Pen pen = new Pen(HighlightColor, 1.6f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
    }

    private void DrawHandle(Graphics g, int x, bool dragged, bool keyboardActive)
    {
        using (Pen line = new Pen(HighlightColor, 2f))
            g.DrawLine(line, x, 13, x, Height - 6);
        int radius = dragged || keyboardActive ? 8 : 7;
        Rectangle knob = new Rectangle(x - radius, Height / 2 - radius, radius * 2, radius * 2);
        if (keyboardActive)
        {
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, HighlightColor)))
                g.FillEllipse(glow, Rectangle.Inflate(knob, 4, 4));
        }
        using (SolidBrush fill = new SolidBrush(Color.White))
        using (Pen pen = new Pen(HighlightColor, dragged ? 3f : 2.2f))
        {
            g.FillEllipse(fill, knob);
            g.DrawEllipse(pen, knob);
        }
    }
}

// Controle de volume compacto: alto-falante (clique = mudo) + barrinha arrastável.
internal sealed class VolumeControl : Control
{
    private const int IconWidth = 26;
    private int volume = 70;
    private bool muted;
    private bool dragging;

    public Color IconColor { get; set; }
    public Color TrackColor { get; set; }
    public Color AccentColor { get; set; }
    public event EventHandler VolumeChanged;

    public int Volume
    {
        get { return volume; }
        set
        {
            int v = Math.Max(0, Math.Min(100, value));
            if (v == volume) return;
            volume = v;
            Invalidate();
            if (VolumeChanged != null) VolumeChanged(this, EventArgs.Empty);
        }
    }

    public bool Muted
    {
        get { return muted; }
        set
        {
            if (muted == value) return;
            muted = value;
            Invalidate();
            if (VolumeChanged != null) VolumeChanged(this, EventArgs.Empty);
        }
    }

    public VolumeControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        IconColor = Color.FromArgb(100, 116, 139);
        TrackColor = Color.FromArgb(203, 213, 225);
        AccentColor = Theme.Accent;
    }

    private int TrackLeft { get { return IconWidth + 6; } }
    private int TrackRight { get { return Width - 8; } }

    private void SetFromX(int x)
    {
        int width = Math.Max(1, TrackRight - TrackLeft);
        int v = (int)Math.Round((double)(Math.Max(TrackLeft, Math.Min(x, TrackRight)) - TrackLeft) * 100 / width);
        if (muted) Muted = false;
        Volume = v;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (CanFocus) Focus();
        if (e.X < IconWidth + 2) { Muted = !muted; return; }
        dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (muted) Muted = false;
        Volume = volume + (e.Delta > 0 ? 5 : -5);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < IconWidth + 20 || Height < 10) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int mid = Height / 2;
        bool silent = muted || volume == 0;

        // Alto-falante
        Color iconColor = silent ? IconColor : AccentColor;
        using (SolidBrush brush = new SolidBrush(iconColor))
        {
            PointF[] speaker = new PointF[]
            {
                new PointF(4, mid - 3), new PointF(8, mid - 3), new PointF(13, mid - 7),
                new PointF(13, mid + 7), new PointF(8, mid + 3), new PointF(4, mid + 3)
            };
            g.FillPolygon(brush, speaker);
        }
        using (Pen pen = new Pen(iconColor, 1.6f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            if (silent)
            {
                g.DrawLine(pen, 17, mid - 4, 23, mid + 4);
                g.DrawLine(pen, 23, mid - 4, 17, mid + 4);
            }
            else
            {
                g.DrawArc(pen, 11, mid - 4, 8, 8, -50, 100);
                if (volume > 50) g.DrawArc(pen, 11, mid - 8, 14, 16, -50, 100);
            }
        }

        // Barra
        int left = TrackLeft;
        int width = Math.Max(1, TrackRight - left);
        using (GraphicsPath track = RoundedPanel.MakePath(new Rectangle(left, mid - 2, width, 4), 2))
        using (SolidBrush trackBrush = new SolidBrush(TrackColor))
            g.FillPath(trackBrush, track);
        int shown = silent ? 0 : volume;
        int knobX = left + width * shown / 100;
        if (knobX - left > 1)
        {
            using (GraphicsPath fill = RoundedPanel.MakePath(new Rectangle(left, mid - 2, knobX - left, 4), 2))
            using (SolidBrush fillBrush = new SolidBrush(AccentColor))
                g.FillPath(fillBrush, fill);
        }
        Rectangle knob = new Rectangle(knobX - 6, mid - 6, 12, 12);
        using (SolidBrush white = new SolidBrush(Color.White))
        using (Pen border = new Pen(silent ? IconColor : AccentColor, 2f))
        {
            g.FillEllipse(white, knob);
            g.DrawEllipse(border, knob);
        }
    }
}

internal sealed class WaveformData
{
    public float[] Peaks;
    public string AudioPath;
}

// Toca a prévia com o próprio Windows (MCI), sem bibliotecas extras.
internal sealed class AudioPreviewPlayer
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int MciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", EntryPoint = "mciGetErrorStringW", CharSet = CharSet.Unicode)]
    private static extern bool MciGetErrorString(int errorCode, StringBuilder errorText, int errorTextLength);

    private const string Alias = "ytdownloaderpreview";
    private string openPath;
    private int lengthMs;

    public string LastError { get; private set; }
    public int LengthMs { get { return lengthMs; } }

    private int Send(string command)
    {
        int result = MciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0) LastError = DescribeError(result);
        return result;
    }

    private string Query(string command)
    {
        StringBuilder value = new StringBuilder(128);
        int result = MciSendString(command, value, value.Capacity, IntPtr.Zero);
        return result == 0 ? value.ToString().Trim() : "";
    }

    private static string DescribeError(int code)
    {
        StringBuilder text = new StringBuilder(256);
        return MciGetErrorString(code, text, text.Capacity) && text.Length > 0
            ? text.ToString() + " (" + code.ToString(CultureInfo.InvariantCulture) + ")"
            : "Erro MCI " + code.ToString(CultureInfo.InvariantCulture);
    }

    public bool Open(string path)
    {
        if (String.Equals(openPath, path, StringComparison.OrdinalIgnoreCase)) return true;
        Close();
        LastError = null;
        if (!File.Exists(path)) { LastError = "Arquivo de prévia não encontrado."; return false; }
        if (Send("open \"" + path + "\" type mpegvideo alias " + Alias) != 0 &&
            Send("open \"" + path + "\" alias " + Alias) != 0) return false;
        Send("set " + Alias + " time format milliseconds");
        int length;
        lengthMs = Int32.TryParse(Query("status " + Alias + " length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out length) ? length : 0;
        openPath = path;
        return true;
    }

    // Toca de fromMs até toMs, sempre dentro da duração real do arquivo.
    // Quando o fim pedido é o fim do arquivo, toca até o final sem "to" (o Windows recusa um fim além do comprimento).
    public bool Play(int fromMs, int toMs)
    {
        if (openPath == null) return false;
        int limit = lengthMs > 0 ? lengthMs : Int32.MaxValue;
        int from = Math.Max(0, Math.Min(fromMs, limit - 50));
        string command = "play " + Alias + " from " + from.ToString(CultureInfo.InvariantCulture);
        if (toMs < limit - 30) command += " to " + Math.Max(from + 1, toMs).ToString(CultureInfo.InvariantCulture);
        return Send(command) == 0;
    }

    public void Stop()
    {
        if (openPath != null) Send("stop " + Alias);
    }

    // level: 0 a 1000
    public void SetVolume(int level)
    {
        if (openPath == null) return;
        int v = Math.Max(0, Math.Min(1000, level));
        MciSendString("setaudio " + Alias + " volume to " + v.ToString(CultureInfo.InvariantCulture), null, 0, IntPtr.Zero);
    }

    public void Close()
    {
        if (openPath == null) return;
        MciSendString("stop " + Alias, null, 0, IntPtr.Zero);
        MciSendString("close " + Alias, null, 0, IntPtr.Zero);
        openPath = null;
        lengthMs = 0;
    }

    public string Mode
    {
        get { return openPath == null ? "" : Query("status " + Alias + " mode").ToLowerInvariant(); }
    }

    public int PositionMs
    {
        get
        {
            if (openPath == null) return 0;
            int value;
            return Int32.TryParse(Query("status " + Alias + " position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }
}

// Um arquivo na janela "Converter".
internal sealed class ConvertItem
{
    public string Path;
    public string Kind;
    public string VideoCodec;
    public string AudioCodec;
    public bool CoverOnly;
    public double Duration;
    public volatile bool Probed;
    public volatile bool Running;
    public volatile bool Done;
    public volatile bool Cancelled;
    public bool Lossless;
    public double Percent;
    public string Output;
    public string Error;
    public Process Process;
    public Label StatusLabel;
    public PercentBar Bar;
}

// Um trecho marcado de um vídeo (início/fim em segundos + fades).
internal sealed class TrimSegment
{
    public int Start;
    public int End;
    public bool Full;
    public double FadeIn;
    public double FadeOut;
}

// O que um item da fila vai gerar: link/arquivo, trecho, tipo, formato e qualidade.
internal sealed class JobSpec
{
    public string Url;
    public int[] Range;
    public double FadeIn;
    public double FadeOut;
    public bool IsVideo;
    public bool IsLocal;
    public bool Subtitles;
    public string Format;
    public string Quality;
}

// Estado de um item da fila enquanto ele baixa (vários podem rodar ao mesmo tempo).
internal sealed class DownloadJob
{
    public JobSpec Spec;
    public string InfoJson;
    public readonly object Lock = new object();
    public readonly Dictionary<string, int> Stages = new Dictionary<string, int>();
    public readonly StringBuilder Errors = new StringBuilder();
    public readonly List<string> Saved = new List<string>();
    public int Index;
    public string Url;
    public string Args;
    public string TempDir;
    public DateTime StartedAt;
    public Process Process;
    public double Percent;
    public double SpeedBytes;
    public int EtaSeconds = -1;
    public int[] TrimRange;
    public double FadeIn;
    public double FadeOut;
    public volatile bool Processing;
    public volatile bool TimedOut;
    public string Title;
    public long ExpectedBytes;
    public DateTime LastActivity;
    public long LastBytes;
    public DateTime LastBytesAt;
    public double MeasuredSpeed;
    public double SectionSeconds;
}

internal sealed class DownloaderForm : Form
{
    internal const string AppVersion = "6.0";
    private readonly Label versionLabel = new Label();
    private readonly BrandMark brandMark = new BrandMark();
    private readonly SegmentedControl typeSwitch = new SegmentedControl();
    // "Baixar ao colar": cola o link e o download começa sozinho assim que o vídeo é identificado.
    private readonly CheckBox autoDownloadCheckBox = new ToggleSwitch();
    private bool autoDownloadArmed;
    // Animações de layout: 0 = escondido, 1 = visível (a prévia e o painel de corte "abrem" suavemente).
    private double previewReveal = 1;
    private double trimReveal;
    private readonly object previewRevealKey = new object();
    private readonly object trimRevealKey = new object();
    private readonly QueueTextBox urlBox = new QueueTextBox();
    private readonly RoundedPanel urlSurface = new RoundedPanel(10);
    private readonly ComboBox typeBox = new ComboBox();
    private readonly RoundedPanel typeSurface = new RoundedPanel(10);
    private readonly ComboBox formatBox = new ComboBox();
    private readonly RoundedPanel formatSurface = new RoundedPanel(10);
    private readonly ComboBox qualityBox = new ComboBox();
    private readonly RoundedPanel qualitySurface = new RoundedPanel(10);
    private readonly TextBox folderBox = new TextBox();
    private readonly RoundedPanel folderSurface = new RoundedPanel(10);
    private readonly RoundedButton downloadButton = new RoundedButton(14);
    private readonly RoundedButton browseButton = new RoundedButton(10);
    private readonly RoundedButton themeButton = new RoundedButton(12);
    private readonly RoundedButton queueButton = new RoundedButton(10);
    private readonly RoundedButton historyButton = new RoundedButton(10);
    private readonly NotifyIcon notifyIcon = new NotifyIcon();
    private readonly PercentBar progress = new PercentBar();
    private readonly Label percentLabel = new Label();
    private readonly Label queueProgressLabel = new Label();
    private readonly RoundedPanel previewCard = new RoundedPanel(14);
    private readonly RoundedThumbnail thumbnail = new RoundedThumbnail();
    private readonly Label titleLabel = new Label();
    private readonly Label subtitleLabel = new Label();
    private readonly Label linkLabel = new Label();
    private readonly Label typeLabel = new Label();
    private readonly Label formatLabel = new Label();
    private readonly Label qualityLabel = new Label();
    private readonly Label folderLabel = new Label();
    private readonly Label videoTitleLabel = new Label();
    private readonly Label videoDetailsLabel = new Label();
    private readonly Label musicStatusLabel = new Label();
    private readonly ToolTip musicStatusTip = new ToolTip();
    private readonly Timer previewTimer = new Timer();
    private int previewRequestId;
    private readonly List<string> downloadQueue = new List<string>();
    private readonly object queueInfoCacheLock = new object();
    private readonly Dictionary<string, QueueVideoInfo> queueInfoCache = new Dictionary<string, QueueVideoInfo>(StringComparer.OrdinalIgnoreCase);
    private volatile bool queueCancelled;
    private readonly StringBuilder queueFailures = new StringBuilder();
    private int queueCompleted;
    private int queueFailed;
    private string queueDestination;
    private string queueFormat;
    private bool darkMode { get { return Theme.Dark; } set { Theme.Dark = value; } }
    private bool isDownloading;
    private bool suppressBatchCountAfterReset;
    private bool previewShown = true;
    private readonly RoundedPanel trimPanel = new RoundedPanel(12);
    private readonly CheckBox trimCheckBox = new ToggleSwitch();
    private readonly TimeRangeSlider timeRangeSlider = new TimeRangeSlider();
    private readonly Label trimStartLabel = new Label();
    private readonly Label trimEndLabel = new Label();
    private readonly Label trimInfoLabel = new Label();
    private readonly RoundedPanel trimTargetSurface = new RoundedPanel(10);
    private readonly ComboBox trimTargetBox = new ComboBox();
    private readonly RoundedButton trimResetButton = new RoundedButton(10);
    private readonly Label trimSummaryLabel = new Label();
    // Trecho escolhido para cada link: { início, fim } em segundos. Só guarda vídeos realmente cortados.
    private readonly Dictionary<string, int[]> trimRanges = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
    private string trimTargetLink;
    private const int MaxParallelDownloads = 3;
    private readonly List<DownloadJob> runningJobs = new List<DownloadJob>();
    private int nextJobIndex;
    private readonly List<string> failedLinks = new List<string>();
    private readonly Timer jobProgressTimer = new Timer();
    private bool queueNormalize;
    private readonly CheckBox normalizeCheckBox = new ToggleSwitch();
    private readonly Dictionary<string, double[]> trimFades = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
    private readonly RoundedPanel trimStartSurface = new RoundedPanel(8);
    private readonly RoundedPanel trimEndSurface = new RoundedPanel(8);
    private readonly TextBox trimStartBox = new TextBox();
    private readonly TextBox trimEndBox = new TextBox();
    private readonly RoundedButton trimPlayButton = new RoundedButton(10);
    private readonly AudioPreviewPlayer previewPlayer = new AudioPreviewPlayer();
    private readonly Timer previewPlayTimer = new Timer();
    private readonly object waveLock = new object();
    private readonly Dictionary<string, WaveformData> waveCache = new Dictionary<string, WaveformData>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> waveLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> waveFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly string previewDirectory = Path.Combine(Path.GetTempPath(), "YTDownloaderPreview");
    private string playingLink;
    private bool previewPaused;
    private int previewEndMs;
    private DateTime queueStartedAt;
    private readonly RoundedButton updateButton = new RoundedButton(12);
    private bool engineUpdating;
    private string engineLastCheck = "";
    private readonly Dictionary<string, List<TrimSegment>> savedSegments = new Dictionary<string, List<TrimSegment>>(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLayoutPanel trimSegmentsFlow = new FlowLayoutPanel();
    private readonly RoundedButton addSegmentButton = new RoundedButton(10);
    private readonly RoundedButton clipboardButton = new RoundedButton(10);
    private readonly RoundedButton localFileButton = new RoundedButton(10);
    private readonly CheckBox subtitlesCheckBox = new ToggleSwitch();
    private readonly List<JobSpec> jobSpecs = new List<JobSpec>();
    private readonly List<JobSpec> failedSpecs = new List<JobSpec>();
    private List<JobSpec> pendingRetrySpecs;
    private bool trayHintShown;
    private readonly RoundedButton thumbnailSaveButton = new RoundedButton(9);
    private readonly Timer thumbnailFeedbackTimer = new Timer();
    private readonly object thumbnailLock = new object();
    private bool savingThumbnails;
    private string revealOnBalloonClick;
    private readonly RoundedPanel toastPanel = new RoundedPanel(12);
    private readonly Label toastTitle = new Label();
    private readonly Label toastDetail = new Label();
    private readonly RoundedButton toastButton = new RoundedButton(9);
    private readonly Timer toastTimer = new Timer();
    private string toastRevealPath;
    private Action toastAction;
    private readonly Label sizeEstimateLabel = new Label();
    private readonly RoundedButton converterButton = new RoundedButton(12);
    private readonly Timer estimateTimer = new Timer();
    private int engineState; // 0 = não verificado hoje, 1 = atualizado, 2 = última verificação falhou
    private double smoothedEtaSeconds = -1;
    private int previewStartedTick;
    private double pausedSeconds;
    private readonly VolumeControl volumeControl = new VolumeControl();
    private bool updatingTrimUi;

    private string AppFolder { get { return Program.RuntimeDirectory; } }

    public DownloaderForm()
    {
        Text = "YT Downloader - by revmpt  ·  v" + AppVersion;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(736, 599);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9F);
        darkMode = ReadWindowsTheme();

        DoubleBuffered = true;
        brandMark.SetBounds(28, 18, 34, 34);
        brandMark.Cursor = Cursors.Hand;
        brandMark.Click += delegate { brandMark.Bounce(); };
        Controls.Add(brandMark);

        titleLabel.Text = "YT Downloader";
        titleLabel.Font = Theme.Semibold(15F);
        titleLabel.AutoSize = true;
        titleLabel.Location = new Point(70, 14);
        Controls.Add(titleLabel);

        versionLabel.Text = "v" + AppVersion;
        versionLabel.Text = "by revmpt  ·  v" + AppVersion;
        versionLabel.Font = new Font("Segoe UI", 8.25F);
        versionLabel.AutoSize = true;
        versionLabel.Location = new Point(72, 38);
        Controls.Add(versionLabel);

        subtitleLabel.Text = "";
        subtitleLabel.Font = new Font("Segoe UI", 8.5F);
        subtitleLabel.AutoSize = true;
        subtitleLabel.Location = new Point(30, 61);
        subtitleLabel.Visible = false;
        Controls.Add(subtitleLabel);

        themeButton.SetBounds(652, 17, 40, 34);
        themeButton.Cursor = Cursors.Hand;
        themeButton.Click += delegate { SwitchThemeAnimated(); };
        Controls.Add(themeButton);

        foreach (RoundedButton ghost in new RoundedButton[] { themeButton, historyButton, updateButton, converterButton }) ghost.Look = ButtonLook.Ghost;
        historyButton.Text = "↓  Downloads";
        historyButton.SetBounds(534, 17, 110, 34);
        historyButton.Cursor = Cursors.Hand;
        historyButton.Click += ShowDownloadHistory;
        Controls.Add(historyButton);

        updateButton.Text = "↻  Atualizar";
        updateButton.SetBounds(422, 17, 104, 34);

        converterButton.Text = "⇄  Converter";
        converterButton.SetBounds(310, 17, 104, 34);
        converterButton.Click += ShowConverter;
        Controls.Add(converterButton);
        musicStatusTip.SetToolTip(converterButton, "Converter vídeos, músicas e imagens do seu PC (MKV → MP4, JPG → PNG, MP3 → AAC…)");
        updateButton.Cursor = Cursors.Hand;
        updateButton.Click += delegate { UpdateEngine(true); };
        Controls.Add(updateButton);
        musicStatusTip.SetToolTip(updateButton, "Atualiza o motor de download (yt-dlp). O app faz isso sozinho uma vez por dia.");
        if (!IsEngineUpdateDue())
        {
            engineState = 1;
            DateTime lastCheck;
            if (DateTime.TryParse(engineLastCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastCheck))
                musicStatusTip.SetToolTip(updateButton, "Motor de download atualizado · verificado em " +
                    lastCheck.ToLocalTime().ToString("dd/MM 'às' HH:mm", CultureInfo.CurrentCulture) +
                    "\r\nClique para verificar de novo. O app verifica sozinho uma vez por dia.");
        }

        // Manutenção ao abrir: limpa sobras antigas e, uma vez por dia, atualiza o motor de download.
        Shown += delegate
        {
            string folder = folderBox.Text.Trim();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                // "Aquece" o motor de download: o primeiro link colado é identificado mais rápido.
                try
                {
                    bool warmed;
                    RunTool(Program.YtDlpPath, "--version", 15000, out warmed, null);
                }
                catch { }
                CleanupOldRuntimes();
                CleanupLeftoverTempFolders(folder);
                System.Threading.Thread.Sleep(2500);
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)delegate { if (IsEngineUpdateDue()) UpdateEngine(false); }); }
                catch { }
            });
        };

        ConfigureLabel(linkLabel, "Links ou arquivos · um por linha", 30, 63);
        musicStatusTip.SetToolTip(linkLabel, "Aceita YouTube, TikTok, Instagram, X/Twitter e outros sites, além de vídeos e músicas do seu PC.");
        urlSurface.SetBounds(28, 85, 392, 40);
        urlSurface.BorderColor = Color.FromArgb(203, 213, 225);
        urlBox.SetBounds(12, 3, 368, 34);
        urlBox.Font = new Font("Segoe UI", 10F);
        urlBox.Multiline = true;
        urlBox.AcceptsReturn = true;
        urlBox.ScrollBars = ScrollBars.Vertical;
        urlBox.BorderStyle = BorderStyle.None;
        urlBox.TextChanged += UrlBoxTextChanged;
        urlSurface.Controls.Add(urlBox);
        Controls.Add(urlSurface);

        AllowDrop = true;
        DragEnter += FormDragEnter;
        DragDrop += FormDragDrop;
        urlBox.AllowDrop = true;
        urlBox.DragEnter += FormDragEnter;
        urlBox.DragDrop += FormDragDrop;
        FormClosed += delegate { try { notifyIcon.Visible = false; notifyIcon.Dispose(); } catch { } };
        notifyIcon.BalloonTipClicked += delegate
        {
            string reveal = revealOnBalloonClick;
            revealOnBalloonClick = null;
            if (reveal != null && File.Exists(reveal))
            {
                try { Process.Start("explorer.exe", "/select,\"" + reveal.Replace("\"", "") + "\""); }
                catch { }
            }
            else RestoreFromTray();
        };
        notifyIcon.BalloonTipClosed += delegate { revealOnBalloonClick = null; };
        notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        // Minimizar durante um download: o app vai para perto do relógio e continua baixando.
        Resize += delegate { if (WindowState == FormWindowState.Minimized && isDownloading && Visible) MinimizeToTray(); };
        Activated += delegate { CheckClipboardForLinks(); };
        // Abre com fade e sobe alguns pixels.
        Motion.FadeIn(this);
        Shown += delegate
        {
            try
            {
                string previous = Application.ExecutablePath + ".antigo";
                if (File.Exists(previous)) File.Delete(previous);
            }
            catch { }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                System.Threading.Thread.Sleep(6000);
                CheckAppUpdate();
            });
        };
        Shown += delegate
        {
            CheckClipboardForLinks();
            EnsureShortcuts();
        };

        autoDownloadCheckBox.Text = "Baixar ao colar";
        autoDownloadCheckBox.Font = new Font("Segoe UI", 8.5F);
        autoDownloadCheckBox.AutoSize = true;
        autoDownloadCheckBox.Location = new Point(692 - autoDownloadCheckBox.PreferredSize.Width, 57);
        autoDownloadCheckBox.CheckedChanged += delegate
        {
            if (!Visible) return;
            Dictionary<string, string> settings = ReadSettings();
            settings["auto-download"] = autoDownloadCheckBox.Checked ? "1" : "0";
            WriteSettings(settings);
            if (autoDownloadCheckBox.Checked && !isDownloading && ReadLinks().Count == 0)
                ShowToast("Baixar ao colar ligado", "Cole um link e o download começa sozinho, com o tipo e o formato escolhidos.", null);
        };
        Controls.Add(autoDownloadCheckBox);
        musicStatusTip.SetToolTip(autoDownloadCheckBox, "Cole um link e o download começa sozinho assim que o vídeo for identificado, com o tipo, o formato e a qualidade escolhidos.");

        localFileButton.Text = "＋  Arquivo";
        localFileButton.SetBounds(428, 85, 104, 40);
        musicStatusTip.SetToolTip(localFileButton, "Escolher vídeos ou músicas do seu PC (também dá para arrastar para cá)");
        localFileButton.Click += BrowseLocalFiles;
        Controls.Add(localFileButton);

        clipboardButton.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        clipboardButton.Visible = false;
        clipboardButton.Click += delegate
        {
            AddLinesToQueue(ClipboardLinks());
            clipboardButton.Visible = false;
        };
        Controls.Add(clipboardButton);

        queueButton.Text = "Fila (0)";
        queueButton.SetBounds(540, 85, 152, 40);
        queueButton.Cursor = Cursors.Hand;
        queueButton.Enabled = false;
        queueButton.Click += ShowQueue;
        Controls.Add(queueButton);

        previewCard.SetBounds(28, 141, 664, 126);
        previewCard.BorderColor = Color.FromArgb(226, 232, 240);
        Controls.Add(previewCard);

        thumbnail.SetBounds(16, 16, 160, 92);
        previewCard.Controls.Add(thumbnail);

        videoTitleLabel.Text = "Cole um link para identificar o vídeo";
        videoTitleLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        videoTitleLabel.AutoEllipsis = true;
        videoTitleLabel.SetBounds(198, 27, 350, 42);
        previewCard.Controls.Add(videoTitleLabel);

        videoDetailsLabel.Text = "A miniatura e os detalhes aparecerão aqui.";
        videoDetailsLabel.AutoEllipsis = true;
        videoDetailsLabel.SetBounds(200, 75, 438, 26);
        previewCard.Controls.Add(videoDetailsLabel);

        musicStatusLabel.Text = "Aguardando verificação musical";
        musicStatusLabel.Font = new Font("Segoe UI", 8.5F);
        musicStatusLabel.AutoEllipsis = true;
        musicStatusLabel.SetBounds(200, 103, 438, 18);
        previewCard.Controls.Add(musicStatusLabel);

        thumbnailSaveButton.Text = "↓  Miniatura";
        thumbnailSaveButton.Font = new Font("Segoe UI", 8.5F);
        thumbnailSaveButton.SetBounds(552, 14, 98, 28);
        thumbnailSaveButton.Enabled = false;
        thumbnailSaveButton.Click += delegate { SaveThumbnails(); };
        previewCard.Controls.Add(thumbnailSaveButton);
        musicStatusTip.SetToolTip(thumbnailSaveButton, "Salva a miniatura (capa) na maior qualidade disponível, na pasta de destino");
        // Aviso rápido dentro do app (não depende das notificações do Windows).
        toastPanel.Visible = false;
        toastPanel.BorderColor = Theme.Accent;
        toastTitle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        toastTitle.AutoEllipsis = true;
        toastTitle.SetBounds(14, 6, 380, 18);
        toastPanel.Controls.Add(toastTitle);
        toastDetail.Font = new Font("Segoe UI", 8F);
        toastDetail.AutoEllipsis = true;
        toastDetail.SetBounds(14, 24, 380, 16);
        toastPanel.Controls.Add(toastDetail);
        toastButton.Text = "Abrir pasta";
        toastButton.Font = new Font("Segoe UI", 8.5F);
        toastButton.SetBounds(408, 9, 96, 28);
        toastButton.Click += delegate
        {
            Action action = toastAction;
            if (action != null)
            {
                HideToast();
                action();
                return;
            }
            string reveal = toastRevealPath;
            HideToast();
            if (reveal != null && File.Exists(reveal))
            {
                try { Process.Start("explorer.exe", "/select,\"" + reveal.Replace("\"", "") + "\""); }
                catch { }
            }
        };
        toastPanel.Controls.Add(toastButton);
        toastPanel.Click += delegate { HideToast(); };
        Controls.Add(toastPanel);
        toastTimer.Interval = 4500;
        toastTimer.Tick += delegate { HideToast(); };

        thumbnailFeedbackTimer.Interval = 2500;
        thumbnailFeedbackTimer.Tick += delegate { thumbnailFeedbackTimer.Stop(); UpdateThumbnailButton(); };
        musicStatusTip.SetToolTip(musicStatusLabel, "Regra conservadora: trate conteúdo musical identificado como protegido até confirmar a licença. A etiqueta Música pode corresponder a Content ID ou a uma faixa da Biblioteca de Áudio, que tem termos próprios.");

        ConfigureLabel(typeLabel, "Tipo", 30, 286);
        typeSurface.SetBounds(28, 309, 208, 34);
        typeSurface.BorderColor = Color.FromArgb(203, 213, 225);
        typeBox.SetBounds(12, 4, 184, 26);
        typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        typeBox.FlatStyle = FlatStyle.Flat;
        typeBox.Items.AddRange(new object[] { "Áudio", "Vídeo", "Vídeo + Áudio" });
        typeBox.SelectedIndexChanged += delegate { UpdateFormats(); LayoutForm(); UpdateSizeEstimate(); };
        typeSurface.Controls.Add(typeBox);
        Controls.Add(typeSurface);
        // O tipo agora é escolhido em "pílulas"; a lista antiga continua por baixo guardando a escolha.
        typeSurface.Visible = false;
        typeSwitch.Items = new string[] { "Áudio", "Vídeo", "Vídeo + Áudio" };
        typeSwitch.Font = new Font("Segoe UI", 9F);
        typeSwitch.SetBounds(28, 309, 208, 34);
        typeSwitch.SelectedIndexChanged += delegate { if (typeBox.SelectedIndex != typeSwitch.SelectedIndex) typeBox.SelectedIndex = typeSwitch.SelectedIndex; };
        typeBox.SelectedIndexChanged += delegate { if (typeSwitch.SelectedIndex != typeBox.SelectedIndex) typeSwitch.SelectedIndex = typeBox.SelectedIndex; };
        musicStatusTip.SetToolTip(typeSwitch, "Áudio: só a música · Vídeo: o vídeo com som · Vídeo + Áudio: os dois arquivos (vídeo e MP3)");
        Controls.Add(typeSwitch);

        ConfigureLabel(formatLabel, "Formato", 258, 286);
        formatSurface.SetBounds(256, 309, 208, 34);
        formatSurface.BorderColor = Color.FromArgb(203, 213, 225);
        formatBox.SetBounds(12, 4, 184, 26);
        formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        formatBox.FlatStyle = FlatStyle.Flat;
        formatBox.SelectedIndexChanged += delegate { UpdateQualityOptions(); UpdateSizeEstimate(); };
        formatSurface.Controls.Add(formatBox);
        Controls.Add(formatSurface);

        ConfigureLabel(qualityLabel, "Qualidade", 486, 286);
        qualitySurface.SetBounds(484, 309, 208, 34);
        qualitySurface.BorderColor = Color.FromArgb(203, 213, 225);
        qualityBox.SetBounds(12, 4, 184, 26);
        qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        qualityBox.FlatStyle = FlatStyle.Flat;
        qualitySurface.Controls.Add(qualityBox);
        qualityBox.SelectedIndexChanged += delegate { UpdateSizeEstimate(); };
        estimateTimer.Interval = 150;
        estimateTimer.Tick += delegate { estimateTimer.Stop(); UpdateTrimSummary(); };

        sizeEstimateLabel.Font = new Font("Segoe UI", 8.5F);
        sizeEstimateLabel.TextAlign = ContentAlignment.MiddleRight;
        sizeEstimateLabel.AutoEllipsis = true;
        sizeEstimateLabel.Visible = false;
        Controls.Add(sizeEstimateLabel);
        musicStatusTip.SetToolTip(sizeEstimateLabel, "Estimativa com base nos formatos informados pelo site, no trecho escolhido, no formato e na qualidade. O tamanho real pode variar um pouco.");
        Controls.Add(qualitySurface);

        ConfigureLabel(folderLabel, "Salvar em", 30, 362);
        folderSurface.SetBounds(28, 386, 550, 36);
        folderSurface.BorderColor = Color.FromArgb(203, 213, 225);
        folderBox.SetBounds(12, 7, 526, 22);
        folderBox.Font = new Font("Segoe UI", 9.5F);
        folderBox.BorderStyle = BorderStyle.None;
        folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        folderSurface.Controls.Add(folderBox);
        Controls.Add(folderSurface);

        browseButton.Text = "Escolher…";
        browseButton.SetBounds(588, 385, 104, 38);
        browseButton.Cursor = Cursors.Hand;
        browseButton.Click += BrowseFolder;
        Controls.Add(browseButton);

        progress.SetBounds(28, 448, 608, 14);
        progress.Visible = false;
        progress.Active = false;
        Controls.Add(progress);

        percentLabel.Text = "0%";
        percentLabel.TextAlign = ContentAlignment.MiddleRight;
        percentLabel.SetBounds(646, 443, 46, 23);
        percentLabel.Visible = false;
        Controls.Add(percentLabel);

        queueProgressLabel.AutoSize = true;
        queueProgressLabel.Font = new Font("Segoe UI", 8.5F);
        queueProgressLabel.Location = new Point(28, 466);
        queueProgressLabel.Visible = false;
        Controls.Add(queueProgressLabel);

        downloadButton.Text = "Baixar";
        downloadButton.SetBounds(565, 492, 130, 46);
        downloadButton.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        downloadButton.Cursor = Cursors.Hand;
        downloadButton.Enabled = false;
        downloadButton.Click += StartDownload;
        Controls.Add(downloadButton);

        trimCheckBox.Text = "Cortar trecho";
        trimCheckBox.Font = new Font("Segoe UI", 9F);
        trimCheckBox.AutoSize = true;
        trimCheckBox.Cursor = Cursors.Hand;
        trimCheckBox.Location = new Point(30, 503);
        trimCheckBox.CheckedChanged += delegate { UpdateTrimVisibility(); };
        Controls.Add(trimCheckBox);

        normalizeCheckBox.Text = "Igualar volume";
        normalizeCheckBox.Font = new Font("Segoe UI", 9F);
        normalizeCheckBox.AutoSize = true;
        normalizeCheckBox.Cursor = Cursors.Hand;
        normalizeCheckBox.Location = new Point(150, 503);
        Controls.Add(normalizeCheckBox);
        musicStatusTip.SetToolTip(normalizeCheckBox, "Ajusta o volume do arquivo final para um nível padrão, para músicas de fontes diferentes tocarem na mesma altura.");

        subtitlesCheckBox.Text = "Legendas";
        subtitlesCheckBox.Font = new Font("Segoe UI", 9F);
        subtitlesCheckBox.AutoSize = true;
        subtitlesCheckBox.Cursor = Cursors.Hand;
        subtitlesCheckBox.Location = new Point(280, 503);
        Controls.Add(subtitlesCheckBox);
        musicStatusTip.SetToolTip(subtitlesCheckBox, "Inclui as legendas em português ou inglês dentro do vídeo, quando o vídeo tiver.");

        jobProgressTimer.Interval = 250;
        jobProgressTimer.Tick += delegate { UpdateJobProgressUi(); };

        trimPanel.SetBounds(28, 442, 664, 78);
        trimPanel.Visible = false;
        Controls.Add(trimPanel);

        trimInfoLabel.Text = "Trecho 0:00 – 0:00";
        trimInfoLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        trimInfoLabel.AutoEllipsis = true;
        trimInfoLabel.SetBounds(16, 8, 632, 20);
        trimPanel.Controls.Add(trimInfoLabel);

        timeRangeSlider.SetBounds(14, 40, 636, 58);
        timeRangeSlider.ValueChanged += TrimSliderChanged;
        timeRangeSlider.SeekRequested += delegate(int seconds) { PlayPreviewFrom(seconds); };
        timeRangeSlider.PlayheadNudge += delegate(int delta) { NudgePlayhead(delta); };
        timeRangeSlider.MarkRequested += delegate(bool isStart) { MarkAtPlayhead(isStart); };
        timeRangeSlider.ScrubFinished += delegate(double seconds) { ScrubTo(seconds); };
        trimPanel.Controls.Add(timeRangeSlider);
        musicStatusTip.SetToolTip(timeRangeSlider, "Arraste as alças para cortar · quadradinhos no topo = fade\r\n" +
            "Clique na onda para ouvir dali · arraste na onda para mover a agulha\r\n" +
            "Setas ← → movem a agulha 1 s (Shift = 5 s) · I / O = marcar início / fim na agulha · Ctrl + rodinha = zoom · Espaço = tocar/pausar");

        trimPlayButton.Text = "▶  Ouvir trecho";
        trimPlayButton.SetBounds(528, 6, 120, 30);
        trimPlayButton.Enabled = false;
        trimPlayButton.Click += delegate { TogglePreview(); };
        trimPanel.Controls.Add(trimPlayButton);

        LoadPreviewVolume();
        volumeControl.SetBounds(400, 10, 120, 30);
        volumeControl.VolumeChanged += delegate { ApplyPreviewVolume(); };
        trimPanel.Controls.Add(volumeControl);
        musicStatusTip.SetToolTip(volumeControl, "Volume da prévia · clique no alto-falante para silenciar · use a rodinha do mouse para ajustar");

        KeyPreview = true;
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || e.Modifiers != Keys.None) return;
            if (!trimPanel.Visible || !trimPlayButton.Enabled) return;
            if (ActiveControl != timeRangeSlider && ActiveControl != volumeControl) return;
            TogglePreview();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        previewPlayTimer.Interval = 40;
        previewPlayTimer.Tick += PreviewPlayTick;
        try
        {
            if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory, true);
        }
        catch { }

        trimTargetSurface.SetBounds(16, 12, 500, 46);
        trimTargetSurface.Visible = false;
        trimTargetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        trimTargetBox.FlatStyle = FlatStyle.Flat;
        trimTargetBox.DrawMode = DrawMode.OwnerDrawFixed;
        trimTargetBox.ItemHeight = 34;
        trimTargetBox.MaxDropDownItems = 6;
        trimTargetBox.Cursor = Cursors.Hand;
        trimTargetBox.SetBounds(6, 4, 488, 38);
        trimTargetBox.DrawItem += DrawTrimTargetItem;
        trimTargetBox.SelectedIndexChanged += TrimTargetChanged;
        trimTargetSurface.Controls.Add(trimTargetBox);
        trimPanel.Controls.Add(trimTargetSurface);

        trimResetButton.Text = "Vídeo inteiro";
        trimResetButton.SetBounds(412, 10, 110, 30);
        trimResetButton.Click += delegate
        {
            if (trimTargetLink != null)
            {
                trimRanges.Remove(trimTargetLink);
                trimFades.Remove(trimTargetLink);
                savedSegments.Remove(trimTargetLink);
            }
            timeRangeSlider.ResetView();
            LoadTrimTarget();
            trimTargetBox.Invalidate();
        };
        trimPanel.Controls.Add(trimResetButton);

        trimSegmentsFlow.FlowDirection = FlowDirection.LeftToRight;
        trimSegmentsFlow.WrapContents = false;
        trimSegmentsFlow.AutoScroll = false;
        trimSegmentsFlow.SetBounds(16, 48, 500, 26);
        trimPanel.Controls.Add(trimSegmentsFlow);

        addSegmentButton.Text = "+  Adicionar trecho";
        addSegmentButton.Font = new Font("Segoe UI", 8.5F);
        addSegmentButton.SetBounds(528, 47, 120, 28);
        addSegmentButton.Click += delegate { AddSegment(); };
        trimPanel.Controls.Add(addSegmentButton);
        musicStatusTip.SetToolTip(addSegmentButton, "Guarda o trecho marcado e libera o controle para marcar outro. Cada trecho vira um arquivo.");

        trimSummaryLabel.Font = new Font("Segoe UI", 8F);
        trimSummaryLabel.TextAlign = ContentAlignment.TopCenter;
        trimSummaryLabel.AutoEllipsis = true;
        trimSummaryLabel.Visible = false;
        trimPanel.Controls.Add(trimSummaryLabel);

        trimStartLabel.Text = "Início";
        trimStartLabel.Font = new Font("Segoe UI", 8.5F);
        trimStartLabel.SetBounds(16, 114, 40, 18);
        trimPanel.Controls.Add(trimStartLabel);

        trimEndLabel.Text = "Fim";
        trimEndLabel.Font = new Font("Segoe UI", 8.5F);
        trimEndLabel.TextAlign = ContentAlignment.TopRight;
        trimEndLabel.SetBounds(540, 114, 38, 18);
        trimPanel.Controls.Add(trimEndLabel);

        // Caixinhas para digitar o tempo exato (ex.: 1:23). Enter confirma, Esc desfaz.
        ConfigureTimeBox(trimStartSurface, trimStartBox, true);
        ConfigureTimeBox(trimEndSurface, trimEndBox, false);

        // Começa a identificar o link quase na hora (antes esperava 0,7 s).
        previewTimer.Interval = 120;
        previewTimer.Tick += PreviewTimerTick;
        FormClosing += delegate(object closingSender, FormClosingEventArgs closingArgs)
        {
            if (isDownloading && closingArgs.CloseReason == CloseReason.UserClosing)
            {
                if (!Visible) RestoreFromTray();
                DialogResult answer = MessageBox.Show(this,
                    "Ainda há downloads em andamento. Deseja cancelar os downloads e fechar o app?",
                    "Fechar", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    closingArgs.Cancel = true;
                    return;
                }
            }
            StopAllJobsForExit();
            previewPlayTimer.Stop();
            previewPlayer.Close();
            SavePreviewVolume();
            try { if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory, true); }
            catch { }
            previewTimer.Stop();
            previewRequestId++;
            DisposeThumbnail();
            musicStatusTip.Dispose();
            lock (queueInfoCacheLock)
            {
                foreach (QueueVideoInfo cached in queueInfoCache.Values)
                    if (cached.Thumbnail != null) cached.Thumbnail.Dispose();
                queueInfoCache.Clear();
            }
        };

        foreach (ComboBox combo in new ComboBox[] { formatBox, qualityBox }) ComboStyle.Apply(combo);
        foreach (RoundedPanel field in new RoundedPanel[] { urlSurface, formatSurface, qualitySurface, folderSurface }) field.FocusRing = true;
        previewCard.Elevated = true;
        trimPanel.Elevated = true;
        thumbnail.Radius = 10;
        typeBox.SelectedIndex = 0;
        ApplySavedChoices();
        SetPreviewVisible(false);
        ApplyTheme();
    }

    private void ConfigureTimeBox(RoundedPanel surface, TextBox box, bool isStart)
    {
        surface.SetBounds(isStart ? 56 : 584, 110, 64, 24);
        box.BorderStyle = BorderStyle.None;
        box.TextAlign = HorizontalAlignment.Center;
        box.Font = new Font("Segoe UI", 8.5F);
        box.SetBounds(6, 4, 52, 16);
        box.Text = "0:00";
        box.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitTimeBox(box, isStart);
                box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                box.Text = FormatTime(isStart ? timeRangeSlider.StartValue : timeRangeSlider.EndValue);
                box.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        box.Leave += delegate { CommitTimeBox(box, isStart); };
        box.Enter += delegate { box.BeginInvoke((MethodInvoker)delegate { box.SelectAll(); }); };
        surface.Controls.Add(box);
        trimPanel.Controls.Add(surface);
        musicStatusTip.SetToolTip(box, "Digite o tempo exato (ex.: 1:23 ou 1:02:03) e pressione Enter");
    }

    private static bool TryParseTime(string text, out int seconds)
    {
        seconds = 0;
        if (String.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Trim().Replace('.', ':').Split(':');
        if (parts.Length < 1 || parts.Length > 3) return false;
        int total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            int value;
            if (!Int32.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
            if (i > 0 && value >= 60) return false;
            total = total * 60 + value;
        }
        seconds = total;
        return true;
    }

    private void CommitTimeBox(TextBox box, bool isStart)
    {
        int seconds;
        if (TrimDuration(trimTargetLink) > 0 && timeRangeSlider.Enabled && TryParseTime(box.Text, out seconds))
        {
            seconds = Math.Min(seconds, timeRangeSlider.Maximum);
            if (isStart) timeRangeSlider.StartValue = Math.Min(seconds, timeRangeSlider.EndValue);
            else timeRangeSlider.EndValue = Math.Max(seconds, timeRangeSlider.StartValue);
        }
        box.Text = TrimDuration(trimTargetLink) > 0
            ? FormatTime(isStart ? timeRangeSlider.StartValue : timeRangeSlider.EndValue)
            : "--:--";
    }
    private void UpdateTrimVisibility()
    {
        if (trimCheckBox.Checked)
        {
            lock (waveLock) waveFailed.Clear();
        }
        else StopPreview();
        AnimateReveal(false, previewShown && trimCheckBox.Checked ? 1 : 0);
        LoadTrimTarget();
    }

    // Posiciona tudo que fica abaixo da prévia, de acordo com: prévia visível, corte ligado e quantidade de links.
    private void LayoutForm()
    {
        // Valores animados: a prévia e o painel de corte "abrem" e "fecham" suavemente.
        int off = (int)Math.Round(-144 * (1 - previewReveal));
        bool multi = ReadLinks().Count > 1;
        bool trimOn = trimReveal > 0.01;
        previewCard.Height = Math.Max(1, (int)Math.Round(126 * previewReveal));
        previewCard.Visible = previewReveal > 0.01;

        typeLabel.Top = 286 + off;
        formatLabel.Top = 286 + off;
        qualityLabel.Top = 286 + off;
        typeSurface.Top = 309 + off;
        typeSwitch.Top = 309 + off;
        formatSurface.Top = 309 + off;
        qualitySurface.Top = 309 + off;
        folderLabel.Top = 362 + off;
        folderSurface.Top = 386 + off;
        browseButton.Top = 385 + off;

        int panelHeight;
        trimTargetSurface.Visible = multi;
        trimSummaryLabel.Visible = true;
        if (multi)
        {
            trimTargetSurface.SetBounds(16, 12, 632, 46);
            trimTargetBox.SetBounds(6, 4, 620, 38);
            trimInfoLabel.SetBounds(16, 72, 280, 20);
            volumeControl.SetBounds(300, 67, 106, 30);
            trimResetButton.SetBounds(412, 67, 110, 30);
            trimPlayButton.SetBounds(528, 67, 120, 30);
            trimSegmentsFlow.SetBounds(16, 105, 500, 26);
            addSegmentButton.SetBounds(528, 104, 120, 28);
            timeRangeSlider.SetBounds(14, 140, 636, 58);
            trimStartLabel.SetBounds(16, 208, 40, 18);
            trimStartSurface.SetBounds(56, 204, 64, 24);
            trimSummaryLabel.SetBounds(130, 208, 404, 18);
            trimEndLabel.SetBounds(540, 208, 38, 18);
            trimEndSurface.SetBounds(584, 204, 64, 24);
            panelHeight = 238;
        }
        else
        {
            trimInfoLabel.SetBounds(16, 15, 280, 20);
            volumeControl.SetBounds(300, 10, 106, 30);
            trimResetButton.SetBounds(412, 10, 110, 30);
            trimPlayButton.SetBounds(528, 10, 120, 30);
            trimSegmentsFlow.SetBounds(16, 48, 500, 26);
            addSegmentButton.SetBounds(528, 47, 120, 28);
            timeRangeSlider.SetBounds(14, 83, 636, 58);
            trimStartLabel.SetBounds(16, 151, 40, 18);
            trimStartSurface.SetBounds(56, 147, 64, 24);
            trimEndLabel.SetBounds(540, 151, 38, 18);
            trimEndSurface.SetBounds(584, 147, 64, 24);
            trimSummaryLabel.SetBounds(130, 151, 404, 18);
            panelHeight = 181;
        }
        trimPanel.SetBounds(28, 442 + off, 664, Math.Max(1, (int)Math.Round(panelHeight * trimReveal)));
        trimPanel.Visible = trimOn;
        trimCheckBox.Visible = previewShown;

        int extra = (int)Math.Round((panelHeight + 6) * trimReveal);
        progress.Top = 448 + off + extra;
        percentLabel.Top = 443 + off + extra;
        queueProgressLabel.Top = 466 + off + extra;
        downloadButton.Top = 492 + off + extra;
        trimCheckBox.Top = 503 + off + extra;
        normalizeCheckBox.Top = 503 + off + extra;
        normalizeCheckBox.Left = trimCheckBox.Left + trimCheckBox.PreferredSize.Width + 18;
        normalizeCheckBox.Visible = previewShown;
        subtitlesCheckBox.Top = 503 + off + extra;
        subtitlesCheckBox.Left = normalizeCheckBox.Left + normalizeCheckBox.PreferredSize.Width + 18;
        subtitlesCheckBox.Visible = previewShown && typeBox.SelectedIndex >= 1;

        sizeEstimateLabel.SetBounds(240, downloadButton.Top - 25, 452, 18);
        sizeEstimateLabel.Visible = previewShown && !isDownloading && sizeEstimateLabel.Text.Length > 0;

        Size client = new Size(720, 560 + off + extra);
        if (ClientSize != client)
        {
            MinimumSize = Size.Empty;
            ClientSize = client;
            MinimumSize = Size;
        }
    }

    private static string FormatTime(int totalSeconds)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void UpdateTrimLabels()
    {
        int start = timeRangeSlider.StartValue;
        int end = timeRangeSlider.EndValue;
        int dur = Math.Max(0, end - start);
        string sStr = FormatTime(start);
        string eStr = FormatTime(end);
        string dStr = FormatTime(dur);
        bool known = TrimDuration(trimTargetLink) > 0;
        if (!trimStartBox.Focused) trimStartBox.Text = known ? sStr : "--:--";
        if (!trimEndBox.Focused) trimEndBox.Text = known ? eStr : "--:--";
        trimStartBox.Enabled = known && timeRangeSlider.Enabled;
        trimEndBox.Enabled = known && timeRangeSlider.Enabled;
        if (!known)
        {
            trimInfoLabel.Text = trimTargetLink == null ? "Cole um link para escolher o trecho" : "Carregando a duração do vídeo…";
        }
        else
        {
            string fadeText = "";
            if (timeRangeSlider.FadeIn > 0 || timeRangeSlider.FadeOut > 0)
                fadeText = "  ·  fade " + timeRangeSlider.FadeIn.ToString("0.#", CultureInfo.CurrentCulture) + "s / " +
                    timeRangeSlider.FadeOut.ToString("0.#", CultureInfo.CurrentCulture) + "s";
            trimInfoLabel.Text = "Trecho " + sStr + " – " + eStr + "  ·  " + dStr + fadeText;
        }

        RequestEstimateRefresh();
    }

    // As contas de tamanho rodam ~0,15 s depois da última mudança: arrastar as alças fica leve.
    private void RequestEstimateRefresh()
    {
        estimateTimer.Stop();
        estimateTimer.Start();
    }

    private void UpdateTrimSummary()
    {
        List<string> currentLinks = ReadLinks();
        // Tamanho do que este vídeo vai gerar (todos os trechos, no formato e qualidade escolhidos).
        string targetSize = "";
        int targetFiles = 0;
        int targetUnknown = 0;
        long targetBytes = trimTargetLink == null ? 0 : EstimateBytesFor(trimTargetLink, out targetFiles, out targetUnknown);
        if (targetBytes > 0)
            targetSize = (targetFiles > 1 ? targetFiles.ToString(CultureInfo.InvariantCulture) + " arquivos ≈ " : "Arquivo final ≈ ") + FormatSize(targetBytes);
        if (currentLinks.Count > 1)
        {
            int cut = 0;
            foreach (string link in currentLinks) if (HasTrimEdits(link)) cut++;
            string edited = cut == 0
                ? "Nenhum vídeo editado"
                : cut.ToString(CultureInfo.InvariantCulture) + " de " + currentLinks.Count.ToString(CultureInfo.InvariantCulture) +
                  (cut == 1 ? " editado" : " editados");
            trimSummaryLabel.Text = targetSize.Length > 0 ? edited + "  ·  este vídeo: " + targetSize.Replace("Arquivo final ", "") : edited + " · os demais completos";
            trimSummaryLabel.ForeColor = cut == 0
                ? Theme.Muted
                : Theme.Accent;
        }
        else
        {
            trimSummaryLabel.Text = targetSize;
            trimSummaryLabel.ForeColor = Theme.Muted;
        }
        UpdateSizeEstimate();
    }

    // ===================== Tamanho estimado =====================

    private static List<long[]> ReadFormats(Dictionary<string, object> metadata, double duration)
    {
        List<long[]> result = new List<long[]>();
        object value;
        if (!metadata.TryGetValue("formats", out value)) return result;
        System.Collections.IList formats = value as System.Collections.IList;
        if (formats == null) return result;
        foreach (object entry in formats)
        {
            Dictionary<string, object> format = entry as Dictionary<string, object>;
            if (format == null) continue;
            string vcodec = JsonString(format, "vcodec");
            string acodec = JsonString(format, "acodec");
            string protocol = JsonString(format, "protocol");
            if (protocol == "mhtml") continue;
            bool hasVideo = vcodec.Length > 0 && vcodec != "none";
            bool hasAudio = acodec.Length > 0 && acodec != "none";
            if (!hasVideo && !hasAudio) continue;
            double tbr = JsonDouble(format, "tbr");
            long bytes = JsonLong(format, "filesize");
            if (bytes <= 0) bytes = JsonLong(format, "filesize_approx");
            if (bytes <= 0 && tbr > 0 && duration > 0) bytes = (long)(tbr * 1000 / 8 * duration);
            if (bytes <= 0) continue;
            long kind = hasVideo && hasAudio ? 2 : (hasVideo ? 0 : 1);
            result.Add(new long[] { JsonLong(format, "height"), kind, bytes, (long)tbr });
        }
        return result;
    }

    private static double JsonDouble(Dictionary<string, object> data, string key)
    {
        object value;
        double result;
        return data.TryGetValue(key, out value) && value != null &&
            Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            ? result : 0;
    }

    private static string FormatSize(long bytes)
    {
        double value = bytes;
        string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(value >= 100 || unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static int QualityHeightCap(string quality)
    {
        if (String.IsNullOrEmpty(quality) || quality == "Melhor disponível") return Int32.MaxValue;
        string digits = quality.Replace("4K (2160p)", "2160").Replace("2K (1440p)", "1440").Replace("p", "").Trim();
        int height;
        return Int32.TryParse(digits, out height) ? height : Int32.MaxValue;
    }

    // Estima o tamanho de um item da fila. Retorna -1 quando o site não informa o suficiente.
    private static long EstimateSpecBytes(QueueVideoInfo info, JobSpec spec)
    {
        if (info == null || info.DurationSeconds <= 0) return -1;
        double seconds = spec.Range != null ? Math.Max(0, spec.Range[1] - spec.Range[0]) : info.DurationSeconds;
        double fraction = Math.Min(1.0, seconds / info.DurationSeconds);
        if (!spec.IsVideo)
        {
            // Áudio: o tamanho depende do formato final escolhido, não do original.
            double kbps;
            string format = spec.Format ?? "";
            if (format == "mp3")
            {
                int bitrate;
                kbps = Int32.TryParse((spec.Quality ?? "").Replace(" kbps", "").Trim(), out bitrate) ? bitrate : 320;
            }
            else if (format == "wav") kbps = 1536;
            else if (format == "flac") kbps = 900;
            else
            {
                kbps = format == "m4a" ? 160 : 130;
                if (info.Formats != null)
                    foreach (long[] f in info.Formats)
                        if (f[1] == 1 && f[3] > kbps && f[3] < 400) kbps = f[3];
            }
            return (long)(kbps * 1000 / 8 * seconds) + 120 * 1024;
        }
        if (spec.IsLocal) return info.EstimatedBytes > 0 ? (long)(info.EstimatedBytes * fraction) : -1;
        if (info.Formats == null || info.Formats.Count == 0)
            return info.EstimatedBytes > 0 ? (long)(info.EstimatedBytes * fraction) : -1;
        // Vídeo: melhor imagem dentro do limite de resolução + melhor som (como o yt-dlp escolhe).
        int cap = QualityHeightCap(spec.Quality);
        long[] bestVideo = null;
        long[] bestMuxed = null;
        long bestAudio = 0;
        foreach (long[] f in info.Formats)
        {
            if (f[1] == 1) { if (f[2] > bestAudio) bestAudio = f[2]; continue; }
            if (f[0] > cap) continue;
            if (f[1] == 0)
            {
                if (bestVideo == null || f[0] > bestVideo[0] || (f[0] == bestVideo[0] && f[2] > bestVideo[2])) bestVideo = f;
            }
            else if (bestMuxed == null || f[0] > bestMuxed[0] || (f[0] == bestMuxed[0] && f[2] > bestMuxed[2])) bestMuxed = f;
        }
        long total;
        if (bestVideo != null && (bestMuxed == null || bestVideo[0] >= bestMuxed[0])) total = bestVideo[2] + bestAudio;
        else if (bestMuxed != null) total = bestMuxed[2];
        else return info.EstimatedBytes > 0 ? (long)(info.EstimatedBytes * fraction) : -1;
        return (long)(total * fraction);
    }

    private QueueVideoInfo PeekQueueInfo(string link)
    {
        if (String.IsNullOrEmpty(link)) return null;
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo info;
            return queueInfoCache.TryGetValue(link, out info) ? info : null;
        }
    }

    // Soma o que um link (ou todos, se link = null) vai gerar com as escolhas atuais.
    private long EstimateBytesFor(string link, out int files, out int unknown)
    {
        files = 0;
        unknown = 0;
        if (formatBox.SelectedItem == null || typeBox.SelectedIndex < 0) return 0;
        List<string> links = ReadLinks();
        if (link != null)
        {
            if (!links.Contains(link)) return 0;
            links = new List<string> { link };
        }
        if (links.Count == 0 || !AreAllLinksValid(links)) return 0;
        long total = 0;
        foreach (JobSpec spec in BuildJobSpecs(links))
        {
            files++;
            long bytes = EstimateSpecBytes(PeekQueueInfo(spec.Url), spec);
            if (bytes > 0) total += bytes;
            else unknown++;
        }
        return total;
    }

    private void UpdateSizeEstimate()
    {
        int files = 0;
        int unknown = 0;
        long total = isDownloading ? 0 : EstimateBytesFor(null, out files, out unknown);
        if (isDownloading || total <= 0)
        {
            sizeEstimateLabel.Text = "";
            sizeEstimateLabel.Visible = false;
            return;
        }
        string text = "Tamanho estimado: ≈ " + FormatSize(total);
        if (files > 1) text += "  ·  " + files.ToString(CultureInfo.InvariantCulture) + " arquivos";
        if (unknown > 0) text += "  ·  " + unknown.ToString(CultureInfo.InvariantCulture) + " sem estimativa";
        sizeEstimateLabel.Text = text;
        sizeEstimateLabel.Visible = previewShown;
    }

    // ----- Corte por vídeo -----

    private int TrimDuration(string link)
    {
        if (String.IsNullOrEmpty(link)) return 0;
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo info;
            if (queueInfoCache.TryGetValue(link, out info) && info.DurationSeconds > 0)
                return (int)Math.Ceiling(info.DurationSeconds);
        }
        return 0;
    }

    private bool TryGetTrimRange(string link, out int[] range)
    {
        range = null;
        int[] stored;
        if (String.IsNullOrEmpty(link) || !trimRanges.TryGetValue(link, out stored)) return false;
        if (stored[1] <= stored[0]) return false;
        range = stored;
        return true;
    }

    private void TrimSliderChanged(object sender, EventArgs e)
    {
        if (!updatingTrimUi && trimTargetLink != null)
        {
            int duration = TrimDuration(trimTargetLink);
            if (duration > 0)
            {
                int start = timeRangeSlider.StartValue;
                int end = timeRangeSlider.EndValue;
                if (start <= 0 && end >= duration) trimRanges.Remove(trimTargetLink);
                else trimRanges[trimTargetLink] = new int[] { start, end };
                double fadeIn = timeRangeSlider.FadeIn;
                double fadeOut = timeRangeSlider.FadeOut;
                if (fadeIn <= 0.05 && fadeOut <= 0.05) trimFades.Remove(trimTargetLink);
                else trimFades[trimTargetLink] = new double[] { fadeIn, fadeOut };
                trimResetButton.Enabled = !isDownloading && HasTrimEdits(trimTargetLink);
                trimTargetBox.Invalidate();
            }
        }
        UpdateTrimLabels();
    }

    private double[] GetFades(string link)
    {
        double[] fades;
        return !String.IsNullOrEmpty(link) && trimFades.TryGetValue(link, out fades) ? fades : null;
    }

    private bool HasTrimEdits(string link)
    {
        return !String.IsNullOrEmpty(link) && (trimRanges.ContainsKey(link) || trimFades.ContainsKey(link) || savedSegments.ContainsKey(link));
    }

    private void TrimTargetChanged(object sender, EventArgs e)
    {
        if (updatingTrimUi) return;
        TrimTargetItem item = trimTargetBox.SelectedItem as TrimTargetItem;
        if (item == null) return;
        trimTargetLink = item.Link;
        LoadTrimTarget();
    }

    // Coloca no slider a duração real do vídeo escolhido e o trecho que já foi marcado para ele.
    private void LoadTrimTarget()
    {
        int duration = TrimDuration(trimTargetLink);
        updatingTrimUi = true;
        try
        {
            timeRangeSlider.StartValue = 0;
            if (duration > 0)
            {
                int[] range;
                if (!TryGetTrimRange(trimTargetLink, out range)) range = new int[] { 0, duration };
                timeRangeSlider.Maximum = duration;
                timeRangeSlider.EndValue = Math.Min(range[1], duration);
                timeRangeSlider.StartValue = Math.Min(range[0], timeRangeSlider.EndValue);
                double[] fades = GetFades(trimTargetLink);
                timeRangeSlider.FadeIn = 0;
                timeRangeSlider.FadeOut = fades == null ? 0 : fades[1];
                timeRangeSlider.FadeIn = fades == null ? 0 : fades[0];
            }
            else
            {
                timeRangeSlider.Maximum = 1;
                timeRangeSlider.EndValue = 1;
            }
            timeRangeSlider.Enabled = duration > 0 && !isDownloading;
        }
        finally { updatingTrimUi = false; }
        trimResetButton.Enabled = !isDownloading && HasTrimEdits(trimTargetLink);
        if (playingLink != null && !String.Equals(playingLink, trimTargetLink, StringComparison.OrdinalIgnoreCase)) StopPreview();
        EnsureWaveform(trimTargetLink);
        ApplyWaveformToSlider();
        RefreshSegmentChips();
        addSegmentButton.Enabled = !isDownloading && TrimDuration(trimTargetLink) > 0;
        UpdateTrimLabels();
    }

    // ----- Forma de onda e prévia de áudio -----

    private WaveformData GetWaveform(string link)
    {
        if (String.IsNullOrEmpty(link)) return null;
        lock (waveLock)
        {
            WaveformData data;
            return waveCache.TryGetValue(link, out data) ? data : null;
        }
    }

    private const int WavePreloadLimit = 5;
    private const int WavePreloadMaxSeconds = 30 * 60;

    // Corte ligado: carrega na hora a onda do vídeo escolhido.
    private void EnsureWaveform(string link)
    {
        if (String.IsNullOrEmpty(link) || !trimCheckBox.Checked || isDownloading || !IsYouTubeUrl(link)) return;
        if (TrimDuration(link) <= 0) return;
        StartWaveformLoad(link);
    }

    // Pré-carregamento: assim que os vídeos são identificados, as ondas dos 5 primeiros links
    // (até 30 min cada) vão sendo carregadas, uma de cada vez, para o corte já abrir pronto.
    private void SchedulePreload()
    {
        if (isDownloading || IsDisposed) return;
        lock (waveLock)
        {
            if (waveLoading.Count > 0) return;
        }
        List<string> links = ReadLinks();
        for (int i = 0; i < links.Count && i < WavePreloadLimit; i++)
        {
            string link = links[i];
            if (!IsYouTubeUrl(link)) continue;
            int duration = TrimDuration(link);
            if (duration <= 0 || duration > WavePreloadMaxSeconds) continue;
            lock (waveLock)
            {
                if (waveCache.ContainsKey(link) || waveFailed.Contains(link)) continue;
            }
            StartWaveformLoad(link);
            return;
        }
    }

    private void StartWaveformLoad(string link)
    {
        lock (waveLock)
        {
            if (waveCache.ContainsKey(link) || waveLoading.Contains(link) || waveFailed.Contains(link)) return;
            waveLoading.Add(link);
        }
        if (String.Equals(link, trimTargetLink, StringComparison.OrdinalIgnoreCase)) ApplyWaveformToSlider();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            WaveformData data = null;
            try { data = LoadWaveformData(link); }
            catch { data = null; }
            lock (waveLock)
            {
                waveLoading.Remove(link);
                if (data != null) waveCache[link] = data;
                else waveFailed.Add(link);
            }
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    if (String.Equals(link, trimTargetLink, StringComparison.OrdinalIgnoreCase)) ApplyWaveformToSlider();
                    SchedulePreload();
                });
            }
            catch { }
        });
    }

    private void ApplyWaveformToSlider()
    {
        string link = trimTargetLink;
        WaveformData data = GetWaveform(link);
        bool loading, failed;
        lock (waveLock)
        {
            loading = link != null && waveLoading.Contains(link);
            failed = link != null && waveFailed.Contains(link);
        }
        timeRangeSlider.Peaks = data == null ? null : data.Peaks;
        if (data != null) timeRangeSlider.StatusText = "";
        else if (loading) timeRangeSlider.StatusText = "Carregando forma de onda…";
        else if (failed) timeRangeSlider.StatusText = "Forma de onda indisponível para este vídeo";
        else timeRangeSlider.StatusText = "";
        trimPlayButton.Enabled = data != null && !isDownloading && timeRangeSlider.Enabled;
        UpdatePlayButtonText();
    }

    private void UpdatePlayButtonText()
    {
        if (playingLink == null) trimPlayButton.Text = "▶  Ouvir trecho";
        else if (previewPaused) trimPlayButton.Text = "▶  Continuar";
        else trimPlayButton.Text = "❚❚  Pausar";
        trimPlayButton.Invalidate();
    }

    private void TogglePreview()
    {
        if (playingLink != null && !previewPaused) PausePreview();
        else if (playingLink != null) ResumePreview();
        else PlayPreviewFromMs(timeRangeSlider.StartValue * 1000);
    }

    private void PausePreview()
    {
        if (playingLink == null || previewPaused) return;
        pausedSeconds = previewPlayer.PositionMs / 1000.0;
        previewPlayTimer.Stop();
        previewPlayer.Stop();
        previewPaused = true;
        timeRangeSlider.PlayheadSeconds = pausedSeconds;
        UpdatePlayButtonText();
    }

    // Continua de onde parou; se as alças mudaram e o ponto ficou fora do trecho, recomeça do novo início.
    // Continua de onde a agulha está. Se ela ficou depois do fim do trecho, recomeça do início do trecho.
    private void ResumePreview()
    {
        double position = pausedSeconds;
        if (position >= timeRangeSlider.EndValue - 0.3) position = timeRangeSlider.StartValue;
        PlayPreviewFromMs((int)(position * 1000));
    }

    // I / O: o início ou o fim do trecho vai para onde a agulha está (como nos editores de vídeo).
    private void MarkAtPlayhead(bool isStart)
    {
        if (playingLink == null || TrimDuration(trimTargetLink) <= 0 || isDownloading) return;
        double position = previewPaused ? pausedSeconds : previewPlayer.PositionMs / 1000.0;
        int seconds = Math.Max(0, Math.Min(timeRangeSlider.Maximum, (int)Math.Round(position)));
        if (isStart)
        {
            if (seconds >= timeRangeSlider.EndValue) timeRangeSlider.EndValue = timeRangeSlider.Maximum;
            timeRangeSlider.StartValue = Math.Min(seconds, timeRangeSlider.EndValue);
        }
        else
        {
            if (seconds <= timeRangeSlider.StartValue) timeRangeSlider.StartValue = 0;
            timeRangeSlider.EndValue = Math.Max(seconds, timeRangeSlider.StartValue);
        }
    }

    // Setas: tocando, pula para o novo ponto e continua; parado/pausado, só posiciona a agulha.
    private void NudgePlayhead(int deltaSeconds)
    {
        string link = trimTargetLink;
        int duration = TrimDuration(link);
        if (duration <= 0 || isDownloading || GetWaveform(link) == null) return;
        double current;
        if (playingLink != null && !previewPaused) current = previewPlayer.PositionMs / 1000.0;
        else if (playingLink != null) current = pausedSeconds;
        else current = timeRangeSlider.StartValue;
        double target = Math.Max(0, Math.Min(duration - 0.5, current + deltaSeconds));
        if (playingLink != null && !previewPaused)
        {
            PlayPreviewFromMs((int)(target * 1000));
            return;
        }
        ParkPlayhead(link, target);
    }

    // Deixa a agulha parada num ponto, pronta para o Espaço / Continuar / I / O.
    private void ParkPlayhead(string link, double seconds)
    {
        previewPlayTimer.Stop();
        previewPlayer.Stop();
        playingLink = link;
        previewPaused = true;
        pausedSeconds = seconds;
        timeRangeSlider.PlayheadSeconds = seconds;
        UpdatePlayButtonText();
    }

    // Soltou a agulha depois de arrastar: tocando, continua dali; parado, ela fica posicionada ali.
    private void ScrubTo(double seconds)
    {
        string link = trimTargetLink;
        int duration = TrimDuration(link);
        if (duration <= 0 || isDownloading || GetWaveform(link) == null)
        {
            if (playingLink == null) timeRangeSlider.PlayheadSeconds = -1;
            return;
        }
        double target = Math.Max(0, Math.Min(duration - 0.5, seconds));
        if (playingLink != null && !previewPaused) PlayPreviewFromMs((int)(target * 1000));
        else ParkPlayhead(link, target);
    }

    private void PlayPreviewFrom(int seconds)
    {
        PlayPreviewFromMs(seconds * 1000);
    }

    private void PlayPreviewFromMs(int startMs)
    {
        string link = trimTargetLink;
        WaveformData data = GetWaveform(link);
        int duration = TrimDuration(link);
        if (data == null || duration <= 0 || isDownloading) return;
        int durationMs = duration * 1000;
        startMs = Math.Max(0, Math.Min(startMs, durationMs));
        int endMs = startMs < timeRangeSlider.EndValue * 1000 ? timeRangeSlider.EndValue * 1000 : durationMs;
        if (endMs - startMs < 300) return;
        if (!previewPlayer.Open(data.AudioPath))
        {
            ShowPreviewError();
            return;
        }
        if (previewPlayer.LengthMs > 0) endMs = Math.Min(endMs, previewPlayer.LengthMs);
        if (endMs - startMs < 300) return;
        ApplyPreviewVolume(PreviewFadeGain(startMs / 1000.0));
        if (!previewPlayer.Play(startMs, endMs))
        {
            ShowPreviewError();
            return;
        }
        playingLink = link;
        previewPaused = false;
        previewEndMs = endMs;
        previewStartedTick = Environment.TickCount;
        timeRangeSlider.PlayheadSeconds = startMs / 1000.0;
        previewPlayTimer.Start();
        UpdatePlayButtonText();
    }

    private void ShowPreviewError()
    {
        string detail = previewPlayer.LastError;
        StopPreview();
        previewPlayer.Close();
        MessageBox.Show(this, "Não foi possível tocar a prévia deste áudio." +
            (String.IsNullOrEmpty(detail) ? "" : "\r\n\r\nDetalhe: " + detail),
            "Ouvir trecho", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void StopPreview()
    {
        previewPlayTimer.Stop();
        previewPlayer.Stop();
        playingLink = null;
        previewPaused = false;
        timeRangeSlider.PlayheadSeconds = -1;
        UpdatePlayButtonText();
    }

    // Atualiza a linha de reprodução. Nos primeiros instantes o Windows ainda está "abrindo" o áudio,
    // então só consideramos que parou depois de um pequeno intervalo.
    private void PreviewPlayTick(object sender, EventArgs e)
    {
        if (playingLink == null || previewPaused || timeRangeSlider.IsScrubbing) return;
        int position = previewPlayer.PositionMs;
        string mode = previewPlayer.Mode;
        bool warmingUp = unchecked(Environment.TickCount - previewStartedTick) < 800;
        bool active = mode == "playing" || mode == "seeking" || mode == "not ready" || mode == "";
        if (position >= previewEndMs - 30 || (!warmingUp && !active))
        {
            StopPreview();
            return;
        }
        if (position > 0 || !warmingUp) timeRangeSlider.PlayheadSeconds = position / 1000.0;
        if (timeRangeSlider.FadeIn > 0 || timeRangeSlider.FadeOut > 0) ApplyPreviewVolume(PreviewFadeGain(position / 1000.0));
    }

    // Curva quadrática: o volume soa natural ao arrastar (70% ≈ metade da potência).
    private void ApplyPreviewVolume()
    {
        ApplyPreviewVolume(1.0);
    }

    private void ApplyPreviewVolume(double gain)
    {
        double level = volumeControl.Muted ? 0 : Math.Pow(volumeControl.Volume / 100.0, 2) * 1000 * Math.Max(0, Math.Min(1, gain));
        previewPlayer.SetVolume((int)Math.Round(level));
    }

    // Ganho do fade na posição atual da prévia (assim você já ouve o fade antes de baixar).
    private double PreviewFadeGain(double seconds)
    {
        double start = timeRangeSlider.StartValue;
        double end = timeRangeSlider.EndValue;
        double fadeIn = timeRangeSlider.FadeIn;
        double fadeOut = timeRangeSlider.FadeOut;
        if (seconds < start || seconds > end) return 1.0;
        double gain = 1.0;
        if (fadeIn > 0 && seconds < start + fadeIn) gain = (seconds - start) / fadeIn;
        if (fadeOut > 0 && seconds > end - fadeOut) gain = Math.Min(gain, (end - seconds) / fadeOut);
        return Math.Max(0, Math.Min(1, gain));
    }

    private string SettingsFilePath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine("YT Downloader", "settings.ini"));
        }
    }

    private Dictionary<string, string> ReadSettings()
    {
        Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(SettingsFilePath)) return settings;
            foreach (string line in File.ReadAllLines(SettingsFilePath, Encoding.UTF8))
            {
                string[] parts = line.Split(new char[] { '=' }, 2);
                if (parts.Length == 2) settings[parts[0].Trim()] = parts[1].Trim();
            }
        }
        catch { }
        return settings;
    }

    private void LoadPreviewVolume()
    {
        Dictionary<string, string> settings = ReadSettings();
        string text;
        int value;
        if (settings.TryGetValue("preview-volume", out text) && Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            volumeControl.Volume = value;
        if (settings.TryGetValue("preview-muted", out text)) volumeControl.Muted = text == "1";
    }

    // Restaura a última pasta, tipo, formato, qualidade e "Igualar volume".
    private void ApplySavedChoices()
    {
        Dictionary<string, string> settings = ReadSettings();
        string text;
        try
        {
            if (settings.TryGetValue("folder", out text) && text.Length > 0 && Directory.Exists(text)) folderBox.Text = text;
            if (settings.TryGetValue("type", out text))
            {
                int index = typeBox.Items.IndexOf(text);
                if (index >= 0) typeBox.SelectedIndex = index;
            }
            if (settings.TryGetValue("format", out text))
            {
                int index = formatBox.Items.IndexOf(text);
                if (index >= 0) formatBox.SelectedIndex = index;
            }
            if (settings.TryGetValue("quality", out text) && qualityBox.Enabled)
            {
                int index = qualityBox.Items.IndexOf(text);
                if (index >= 0) qualityBox.SelectedIndex = index;
            }
            if (settings.TryGetValue("normalize", out text)) normalizeCheckBox.Checked = text == "1";
            if (settings.TryGetValue("subtitles", out text)) subtitlesCheckBox.Checked = text == "1";
            if (settings.TryGetValue("auto-download", out text)) autoDownloadCheckBox.Checked = text == "1";
        }
        catch { }
    }

    private void SavePreviewVolume()
    {
        try
        {
            Dictionary<string, string> settings = ReadSettings();
            settings["preview-volume"] = volumeControl.Volume.ToString(CultureInfo.InvariantCulture);
            settings["preview-muted"] = volumeControl.Muted ? "1" : "0";
            settings["folder"] = folderBox.Text.Trim();
            if (typeBox.SelectedItem != null) settings["type"] = typeBox.SelectedItem.ToString();
            if (formatBox.SelectedItem != null) settings["format"] = formatBox.SelectedItem.ToString();
            if (qualityBox.SelectedItem != null) settings["quality"] = qualityBox.SelectedItem.ToString();
            settings["normalize"] = normalizeCheckBox.Checked ? "1" : "0";
            settings["subtitles"] = subtitlesCheckBox.Checked ? "1" : "0";
            if (engineLastCheck.Length > 0) settings["engine-last-check"] = engineLastCheck;
            WriteSettings(settings);
        }
        catch { }
    }

    private void WriteSettings(Dictionary<string, string> settings)
    {
        try
        {
            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in settings)
                builder.Append(pair.Key).Append('=').Append(pair.Value).Append("\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
            File.WriteAllText(SettingsFilePath, builder.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // ===================== Motor de download: atualização e limpeza =====================

    private bool IsEngineUpdateDue()
    {
        string text;
        if (engineLastCheck.Length == 0 && ReadSettings().TryGetValue("engine-last-check", out text)) engineLastCheck = text;
        DateTime last;
        return !DateTime.TryParse(engineLastCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) ||
            (DateTime.Now - last.ToLocalTime()).TotalHours >= 24;
    }

    private void UpdateEngineButtonLook()
    {
        if (engineUpdating) return;
        Color mainText = Theme.Fg;
        if (engineState == 1)
        {
            updateButton.Text = "✓  Atualizado";
            updateButton.ForeColor = Theme.Accent;
        }
        else
        {
            updateButton.Text = "↻  Atualizar";
            updateButton.ForeColor = Theme.Muted;
        }
        updateButton.Invalidate();
    }

    private void UpdateEngine(bool manual)
    {
        if (engineUpdating) return;
        if (isDownloading)
        {
            if (manual) MessageBox.Show(this, "Espere a fila de downloads terminar para atualizar o motor de download.", "Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        engineUpdating = true;
        updateButton.Enabled = false;
        updateButton.Text = "Verificando…";
        updateButton.ForeColor = Theme.Fg;
        string downloader = Program.YtDlpPath;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            bool ok = false;
            bool updated = false;
            string detail = "";
            string version = "";
            try
            {
                ok = UpdateYtDlpEngine(out updated, out detail);
                bool versionOk;
                version = RunTool(Program.YtDlpPath, "--version", 30000, out versionOk, null).Trim();
                if (!versionOk) version = "";
            }
            catch (Exception ex) { detail = ex.Message; }
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    engineUpdating = false;
                    engineState = ok ? 1 : 2;
                    UpdateEngineButtonLook();
                    updateButton.Enabled = !isDownloading;
                    if (ok)
                    {
                        engineLastCheck = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                        SavePreviewVolume();
                    }
                    string versionText = version.Length > 0 ? version : "desconhecida";
                    musicStatusTip.SetToolTip(updateButton, "Motor de download (yt-dlp) versão " + versionText +
                        (ok ? " · verificado às " + DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture) : " · última verificação falhou") +
                        "\r\nClique para verificar de novo. O app verifica sozinho uma vez por dia.");
                    if (!manual) return;
                    if (ok && updated)
                        MessageBox.Show(this, "Motor de download atualizado para a versão " + versionText + ".", "Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else if (ok)
                        MessageBox.Show(this, "O motor de download já está na versão mais recente (" + versionText + ").", "Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this, "Não foi possível atualizar agora. Verifique sua internet e tente de novo." +
                            (detail.Length > 0 ? "\r\n\r\nDetalhe: " + detail : ""), "Atualizar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            catch { }
        });
    }

    // Compara a versão instalada do yt-dlp com a última do GitHub e, se houver nova, baixa e troca com segurança.
    private bool UpdateYtDlpEngine(out bool updated, out string detail)
    {
        updated = false;
        detail = "";
        bool currentOk;
        string current = RunTool(Program.YtDlpPath, "--version", 30000, out currentOk, null).Trim();
        string json;
        using (TimeoutWebClient client = new TimeoutWebClient())
        {
            client.Encoding = Encoding.UTF8;
            client.Headers.Add("User-Agent", "YT-Downloader-by-revmpt");
            client.Headers.Add("Accept", "application/vnd.github+json");
            json = client.DownloadString("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
        }
        Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        string tag = JsonString(release, "tag_name").Trim();
        if (tag.Length == 0 || !Regex.IsMatch(tag, "^[0-9A-Za-z._-]+$"))
        {
            detail = "Resposta inesperada do GitHub.";
            return false;
        }
        if (currentOk && String.Equals(current, tag, StringComparison.OrdinalIgnoreCase)) return true;

        try { BeginInvoke((MethodInvoker)delegate { if (engineUpdating) updateButton.Text = "Atualizando…"; }); }
        catch { }
        string folderName = "yt-dlp-" + tag;
        string target = Path.Combine(Program.RuntimeDirectory, folderName);
        string staging = target + ".extraindo";
        string zipPath = Path.Combine(Path.GetTempPath(), "yt-dlp_win-" + tag + ".zip");
        try
        {
            using (WebClient client = new WebClient())
            {
                client.Headers.Add("User-Agent", "YT-Downloader-by-revmpt");
                client.DownloadFile("https://github.com/yt-dlp/yt-dlp/releases/download/" + tag + "/yt-dlp_win.zip", zipPath);
            }
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);
            string exe = Path.Combine(staging, "yt-dlp.exe");
            bool testOk;
            RunTool(exe, "--version", 30000, out testOk, null);
            if (!File.Exists(exe) || !testOk)
            {
                detail = "A versão nova do motor não abriu; a atual foi mantida.";
                return false;
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            File.WriteAllText(Program.EnginePointerPath, folderName);
            updated = true;
            Program.CleanupOldEngines(folderName);
            return true;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch { }
        }
    }

    private string RunTool(string file, string arguments, int timeoutMs, out bool success, Action<string> onLine)
    {
        success = false;
        StringBuilder output = new StringBuilder();
        object outputLock = new object();
        using (Process process = new Process())
        {
            process.StartInfo.FileName = file;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = AppFolder;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            DataReceivedEventHandler collect = delegate(object source, DataReceivedEventArgs received)
            {
                if (received.Data == null) return;
                lock (outputLock) output.AppendLine(received.Data);
                if (onLine != null)
                {
                    try { onLine(received.Data); }
                    catch { }
                }
            };
            process.OutputDataReceived += collect;
            process.ErrorDataReceived += collect;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); }
                catch { }
                return "Tempo esgotado.";
            }
            process.WaitForExit();
            success = process.ExitCode == 0;
        }
        lock (outputLock) return output.ToString().Trim();
    }

    // Apaga as cópias de ferramentas deixadas por versões antigas do app (cada uma tem ~230 MB).
    private static void CleanupOldRuntimes()
    {
        try
        {
            string current = Program.RuntimeDirectory;
            if (String.IsNullOrEmpty(current)) return;
            string currentFull = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetDirectoryName(currentFull);
            if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
            foreach (string dir in Directory.GetDirectories(parent, "runtime-v*"))
            {
                string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(full, true); }
                catch { }
            }
        }
        catch { }
    }

    // Remove pastas temporárias que sobraram de downloads interrompidos (ex.: PC desligado no meio).
    private static void CleanupLeftoverTempFolders(string folder)
    {
        try
        {
            if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (string dir in Directory.GetDirectories(folder, ".ytdownloader-temp-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) > DateTime.Now.AddHours(-1)) continue;
                    Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string PreviewKey(string link)
    {
        using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(link));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }

    // 1) yt-dlp baixa só um áudio leve; 2) o FFmpeg gera, numa única passada, os picos da onda e um MP3 para ouvir.
    private WaveformData LoadWaveformData(string link)
    {
        string downloader = Program.YtDlpPath;
        string ffmpeg = Path.Combine(AppFolder, "ffmpeg.exe");
        if (!File.Exists(downloader) || !File.Exists(ffmpeg)) return null;
        Directory.CreateDirectory(previewDirectory);
        string key = PreviewKey(link);
        string template = Path.Combine(previewDirectory, key + ".src.%(ext)s");
        bool localSource = IsLocalMedia(link);
        string source = localSource ? link.Trim().Trim('"') : null;

        if (!localSource)
        {
        ProcessStartInfo fetch = new ProcessStartInfo();
        fetch.FileName = downloader;
        fetch.Arguments = "--no-playlist --no-warnings --quiet --no-progress --no-part --no-mtime --socket-timeout 15 --retries 2 " +
            "--concurrent-fragments 4 -f \"ba[abr<=96]/wa/ba/b\" -o \"" + template + "\" " + Quote(link);
        fetch.WorkingDirectory = AppFolder;
        fetch.UseShellExecute = false;
        fetch.CreateNoWindow = true;
        using (Process process = Process.Start(fetch))
        {
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(); }
                catch { }
                return null;
            }
        }

        foreach (string file in Directory.GetFiles(previewDirectory, key + ".src.*"))
        {
            if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)) continue;
            source = file;
            break;
        }
        }
        if (source == null) return null;

        string mp3 = Path.Combine(previewDirectory, key + ".mp3");
        const int sampleRate = 4000;
        const int samplesPerBlock = 100; // 40 picos por segundo
        List<float> blocks = new List<float>();
        try
        {
            ProcessStartInfo decode = new ProcessStartInfo();
            decode.FileName = ffmpeg;
            decode.Arguments = "-nostdin -hide_banner -loglevel error -y -i \"" + source + "\" " +
                "-map 0:a:0 -ac 1 -ar " + sampleRate.ToString(CultureInfo.InvariantCulture) + " -f s16le pipe:1 " +
                "-map 0:a:0 -ac 2 -ar 44100 -c:a libmp3lame -b:a 128k \"" + mp3 + "\"";
            decode.WorkingDirectory = AppFolder;
            decode.UseShellExecute = false;
            decode.CreateNoWindow = true;
            decode.RedirectStandardOutput = true;
            using (Process process = Process.Start(decode))
            {
                Stream output = process.StandardOutput.BaseStream;
                byte[] buffer = new byte[65536];
                int pendingByte = -1;
                int blockPeak = 0;
                int blockCount = 0;
                int read;
                while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int index = 0;
                    if (pendingByte >= 0)
                    {
                        int sample = (short)(pendingByte | (buffer[0] << 8));
                        if (sample < 0) sample = -sample;
                        if (sample > blockPeak) blockPeak = sample;
                        blockCount++;
                        pendingByte = -1;
                        index = 1;
                    }
                    for (; index + 1 < read; index += 2)
                    {
                        int sample = (short)(buffer[index] | (buffer[index + 1] << 8));
                        if (sample < 0) sample = -sample;
                        if (sample > blockPeak) blockPeak = sample;
                        if (++blockCount >= samplesPerBlock)
                        {
                            blocks.Add(blockPeak / 32768f);
                            blockPeak = 0;
                            blockCount = 0;
                        }
                    }
                    if (index < read) pendingByte = buffer[index];
                }
                if (blockCount > 0) blocks.Add(blockPeak / 32768f);
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); }
                    catch { }
                    return null;
                }
                if (process.ExitCode != 0) return null;
            }
        }
        finally
        {
            // Só apaga a cópia temporária baixada; um arquivo do seu PC nunca é apagado.
            if (!localSource)
            {
                try { File.Delete(source); }
                catch { }
            }
        }
        if (blocks.Count == 0 || !File.Exists(mp3)) return null;

        // Normaliza para a onda ocupar a altura toda, mesmo em áudios baixos.
        float max = 0f;
        foreach (float value in blocks) if (value > max) max = value;
        if (max <= 0f) max = 1f;
        float[] peaks = new float[blocks.Count];
        for (int i = 0; i < peaks.Length; i++) peaks[i] = (float)Math.Pow(blocks[i] / max, 0.8);
        return new WaveformData { Peaks = peaks, AudioPath = mp3 };
    }

    // Atualiza a lista "qual vídeo cortar" com títulos, miniaturas e durações já carregados.
    private void RefreshTrimTargets()
    {
        List<string> links = ReadLinks();
        bool multi = links.Count > 1;
        string keep = trimTargetLink;
        if (keep == null || !links.Contains(keep)) keep = links.Count > 0 ? links[0] : null;

        if (!trimTargetBox.DroppedDown)
        {
            updatingTrimUi = true;
            try
            {
                foreach (object old in trimTargetBox.Items)
                {
                    TrimTargetItem oldItem = old as TrimTargetItem;
                    if (oldItem != null && oldItem.Thumbnail != null) oldItem.Thumbnail.Dispose();
                }
                trimTargetBox.Items.Clear();
                if (multi)
                {
                    int selected = 0;
                    for (int i = 0; i < links.Count; i++)
                    {
                        TrimTargetItem item = new TrimTargetItem { Link = links[i], Number = i + 1, Title = "Carregando título…" };
                        QueueVideoInfo info = GetCachedQueueVideoInfo(links[i]);
                        if (info != null)
                        {
                            if (!String.IsNullOrEmpty(info.Title)) item.Title = info.Title;
                            item.DurationSeconds = info.DurationSeconds > 0 ? (int)Math.Ceiling(info.DurationSeconds) : 0;
                            if (info.Thumbnail != null)
                            {
                                item.Thumbnail = MakeSmallThumbnail(info.Thumbnail, 96);
                                info.Thumbnail.Dispose();
                            }
                        }
                        trimTargetBox.Items.Add(item);
                        if (links[i] == keep && selected == 0) selected = i;
                    }
                    trimTargetBox.SelectedIndex = selected;
                }
            }
            finally { updatingTrimUi = false; }
        }

        trimTargetLink = keep;
        LayoutForm();
        LoadTrimTarget();
        SchedulePreload();
    }

    private static Image MakeSmallThumbnail(Image source, int width)
    {
        int height = Math.Max(1, source.Height * width / Math.Max(1, source.Width));
        Bitmap small = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        return small;
    }

    private void DrawTrimTargetItem(object sender, DrawItemEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        bool inEdit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool highlighted = !inEdit && (e.State & DrawItemState.Selected) != 0;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;
        Color accent = Theme.Accent;
        Color back = highlighted ? Theme.AccentSoft : surface;
        using (SolidBrush brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);
        if (e.Index < 0 || e.Index >= trimTargetBox.Items.Count) return;
        TrimTargetItem item = trimTargetBox.Items[e.Index] as TrimTargetItem;
        if (item == null) return;

        Rectangle thumb = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + (e.Bounds.Height - 28) / 2, 50, 28);
        using (GraphicsPath path = RoundedPanel.MakePath(thumb, 5))
        {
            Region previousClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            using (SolidBrush placeholder = new SolidBrush(Theme.Track))
                g.FillRectangle(placeholder, thumb);
            if (item.Thumbnail != null)
            {
                // Preenche a miniatura cortando as bordas (remove as faixas pretas das capas 4:3).
                float targetRatio = (float)thumb.Width / thumb.Height;
                float sourceRatio = (float)item.Thumbnail.Width / item.Thumbnail.Height;
                RectangleF src;
                if (sourceRatio > targetRatio)
                {
                    float w = item.Thumbnail.Height * targetRatio;
                    src = new RectangleF((item.Thumbnail.Width - w) / 2f, 0, w, item.Thumbnail.Height);
                }
                else
                {
                    float h = item.Thumbnail.Width / targetRatio;
                    src = new RectangleF(0, (item.Thumbnail.Height - h) / 2f, item.Thumbnail.Width, h);
                }
                g.DrawImage(item.Thumbnail, thumb, src, GraphicsUnit.Pixel);
            }
            g.Clip = previousClip;
        }

        int textX = thumb.Right + 10;
        int textWidth = e.Bounds.Right - textX - 6;
        string title = item.Number.ToString(CultureInfo.InvariantCulture) + ".  " + item.Title;
        TextRenderer.DrawText(g, title, ItemTitleFont, new Rectangle(textX, e.Bounds.Y + 1, textWidth, 17), mainText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        int[] range;
        bool isCut = TryGetTrimRange(item.Link, out range);
        int savedCount = SavedSegmentCount(item.Link);
        string length = item.DurationSeconds > 0 ? FormatTime(item.DurationSeconds) : "Carregando duração…";
        if (savedCount > 0) length += "  ·  +" + savedCount.ToString(CultureInfo.InvariantCulture) + (savedCount == 1 ? " trecho" : " trechos");
        Rectangle second = new Rectangle(textX, e.Bounds.Y + 17, textWidth, 16);
        Font smallFont = ItemSmallFont;
        {
            if (isCut)
            {
                string cutText = "✂ " + FormatTime(range[0]) + " – " + FormatTime(range[1]);
                Size cutSize = TextRenderer.MeasureText(g, cutText, smallFont, second.Size, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, cutText, smallFont, second, accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Rectangle rest = new Rectangle(second.X + cutSize.Width + 8, second.Y, Math.Max(0, second.Width - cutSize.Width - 8), second.Height);
                TextRenderer.DrawText(g, "de " + length, smallFont, rest, mutedText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                string text = item.DurationSeconds > 0 ? length + (savedCount > 0 ? "" : "  ·  completo") : length;
                TextRenderer.DrawText(g, text, smallFont, second, mutedText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    private static readonly Font ItemTitleFont = new Font("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font ItemSmallFont = new Font("Segoe UI", 8F);

    private static string FormatSecondsForYtDlp(int seconds)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
    }

    private static bool ReadWindowsTheme()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value != null && Convert.ToInt32(value) == 0;
            }
        }
        catch { return false; }
    }

    private void ConfigureLabel(Label label, string text, int x, int y)
    {
        // Rótulos no estilo do ForeverNote: pequenos, em maiúsculas e discretos.
        label.Font = Theme.Semibold(7.75F);
        label.Text = text.ToUpper(CultureInfo.CurrentCulture);
        label.AutoSize = true;
        label.Location = new Point(x, y);
        Controls.Add(label);
    }

    private void ApplyTheme()
    {
        // Neutral charcoal surfaces keep dark mode from reading as navy blue.
        Color background = Theme.Bg;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;
        BackColor = background;
        ForeColor = mainText;
        titleLabel.ForeColor = mainText;
        subtitleLabel.ForeColor = mutedText;
        versionLabel.ForeColor = Theme.Faint;
        linkLabel.ForeColor = Theme.Faint;
        typeLabel.ForeColor = Theme.Faint;
        formatLabel.ForeColor = Theme.Faint;
        qualityLabel.ForeColor = Theme.Faint;
        folderLabel.ForeColor = Theme.Faint;
        brandMark.Invalidate();
        typeSwitch.TrackColor = Theme.Dark ? Theme.Sunken : Theme.Track;
        typeSwitch.PillColor = Theme.Dark ? Motion.Mix(Theme.Raised, Color.White, 0.07) : Theme.Raised;
        typeSwitch.TextColor = mutedText;
        typeSwitch.SelectedTextColor = mainText;
        typeSwitch.Invalidate();
        videoTitleLabel.ForeColor = mainText;
        videoDetailsLabel.ForeColor = mutedText;
        queueProgressLabel.ForeColor = mutedText;
        musicStatusLabel.ForeColor = mutedText;
        previewCard.BackColor = surface;
        previewCard.BorderColor = Theme.Line;
        thumbnail.PlaceholderColor = Theme.Track;
        thumbnail.PlaceholderTextColor = mutedText;
        thumbnail.Invalidate();
        urlBox.BackColor = surface;
        urlBox.ForeColor = mainText;
        urlSurface.BackColor = surface;
        folderSurface.BackColor = surface;
        typeSurface.BackColor = surface;
        formatSurface.BackColor = surface;
        qualitySurface.BackColor = surface;
        Color inputBorder = Theme.LineStrong;
        urlSurface.BorderColor = inputBorder;
        folderSurface.BorderColor = inputBorder;
        typeSurface.BorderColor = inputBorder;
        formatSurface.BorderColor = inputBorder;
        qualitySurface.BorderColor = inputBorder;
        folderBox.BackColor = surface;
        folderBox.ForeColor = mainText;
        typeBox.BackColor = surface;
        typeBox.ForeColor = mainText;
        formatBox.BackColor = surface;
        formatBox.ForeColor = mainText;
        qualityBox.BackColor = surface;
        qualityBox.ForeColor = mainText;
        browseButton.BackColor = surface;
        browseButton.ForeColor = mainText;
        browseButton.BorderColor = Theme.LineStrong;
        progress.TrackColor = Theme.Track;
        progress.FillColor = Theme.Accent;
        progress.BackColor = background;
        percentLabel.ForeColor = mutedText;
        downloadButton.BackColor = Theme.Accent;
        downloadButton.ForeColor = Theme.OnAccent;
        downloadButton.Look = ButtonLook.Primary;
        downloadButton.BorderColor = Color.Transparent;
        trimPanel.BackColor = surface;
        trimPanel.BorderColor = Theme.Line;
        trimCheckBox.ForeColor = mainText;
        trimInfoLabel.ForeColor = mainText;
        trimStartLabel.ForeColor = mutedText;
        trimEndLabel.ForeColor = mutedText;
        normalizeCheckBox.ForeColor = mainText;
        autoDownloadCheckBox.ForeColor = mutedText;
        sizeEstimateLabel.ForeColor = mutedText;
        subtitlesCheckBox.ForeColor = mainText;
        trimSegmentsFlow.BackColor = surface;
        addSegmentButton.BackColor = surface;
        addSegmentButton.ForeColor = mainText;
        addSegmentButton.BorderColor = Theme.LineStrong;
        thumbnailSaveButton.BackColor = surface;
        thumbnailSaveButton.ForeColor = mainText;
        thumbnailSaveButton.BorderColor = Theme.LineStrong;
        localFileButton.BackColor = surface;
        localFileButton.ForeColor = mainText;
        localFileButton.BorderColor = Theme.LineStrong;
        clipboardButton.BackColor = Theme.Accent;
        clipboardButton.ForeColor = Theme.OnAccent;
        clipboardButton.BorderColor = Color.Transparent;
        if (trimPanel.Visible) RefreshSegmentChips();
        Color timeBoxBack = Theme.Sunken;
        Color timeBoxBorder = Theme.LineStrong;
        trimStartSurface.BackColor = timeBoxBack;
        trimEndSurface.BackColor = timeBoxBack;
        trimStartSurface.BorderColor = timeBoxBorder;
        trimEndSurface.BorderColor = timeBoxBorder;
        trimStartBox.BackColor = timeBoxBack;
        trimEndBox.BackColor = timeBoxBack;
        trimStartBox.ForeColor = mainText;
        trimEndBox.ForeColor = mainText;
        trimSummaryLabel.ForeColor = mutedText;
        trimTargetSurface.BackColor = surface;
        trimTargetSurface.BorderColor = Theme.LineStrong;
        trimTargetBox.BackColor = surface;
        trimTargetBox.ForeColor = mainText;
        trimTargetBox.Invalidate();
        trimResetButton.BackColor = surface;
        trimResetButton.ForeColor = mainText;
        trimResetButton.BorderColor = Theme.LineStrong;
        timeRangeSlider.TrackColor = Theme.Track;
        timeRangeSlider.WaveBackColor = Theme.Sunken;
        timeRangeSlider.StatusTextColor = mutedText;
        timeRangeSlider.PlayheadColor = mainText;
        trimPlayButton.BackColor = surface;
        trimPlayButton.ForeColor = Theme.Accent;
        trimPlayButton.BorderColor = Theme.LineStrong;
        volumeControl.IconColor = mutedText;
        volumeControl.TrackColor = Theme.Track;
        volumeControl.AccentColor = Theme.Accent;
        volumeControl.Invalidate();
        timeRangeSlider.HighlightColor = Theme.Accent;
        timeRangeSlider.Invalidate();
        if (trimPanel.Visible) UpdateTrimLabels();
        // Botões do topo no estilo "fantasma" do ForeverNote: sem moldura, acendem ao passar o mouse.
        foreach (RoundedButton ghost in new RoundedButton[] { themeButton, historyButton, updateButton, converterButton })
        {
            ghost.BackColor = background;
            ghost.ForeColor = mutedText;
            ghost.HoverForeColor = mainText;
            ghost.BorderColor = Color.Transparent;
            ghost.Invalidate();
        }
        themeButton.Font = new Font("Segoe UI", 11F);
        themeButton.Text = darkMode ? "☀" : "☾";
        musicStatusTip.SetToolTip(themeButton, darkMode ? "Mudar para o modo claro" : "Mudar para o modo escuro");
        ApplyWindowChrome(this);
        queueButton.BackColor = surface;
        queueButton.ForeColor = mainText;
        queueButton.BorderColor = Theme.LineStrong;
        UpdateEngineButtonLook();
    }

    private void UpdateFormats()
    {
        formatBox.Items.Clear();
        string[] formats = typeBox.SelectedIndex == 0
            ? new string[] { "MP3", "M4A", "WAV", "FLAC", "OPUS" }
            : new string[] { "MP4", "MKV", "WEBM" };
        formatBox.Items.AddRange(formats);
        formatBox.SelectedIndex = 0;
        UpdateQualityOptions();
    }

    private void UpdateQualityOptions()
    {
        if (typeBox.SelectedIndex >= 1)
        {
            qualityLabel.Text = "RESOLUÇÃO MÁXIMA";
            qualityBox.Enabled = true;
            qualityBox.Items.Clear();
            qualityBox.Items.AddRange(new object[] { "Melhor disponível", "4K (2160p)", "2K (1440p)", "1080p", "720p", "480p", "360p" });
            qualityBox.SelectedIndex = 0;
        }
        else if (formatBox.SelectedItem != null && formatBox.SelectedItem.ToString() == "MP3")
        {
            qualityLabel.Text = "BITRATE MP3";
            qualityBox.Enabled = true;
            qualityBox.Items.Clear();
            qualityBox.Items.AddRange(new object[] { "320 kbps", "256 kbps", "192 kbps", "128 kbps" });
            qualityBox.SelectedIndex = 0;
        }
        else
        {
            qualityLabel.Text = "BITRATE";
            qualityBox.Enabled = false;
            qualityBox.Items.Clear();
            qualityBox.Items.Add("Não aplicável");
            qualityBox.SelectedIndex = 0;
        }
    }

    private void FormDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            e.Effect = DragDropEffects.Copy;
        else
            e.Effect = DragDropEffects.None;
    }

    private void FormDragDrop(object sender, DragEventArgs e)
    {
        string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files != null && files.Length > 0)
        {
            AddLinesToQueue(files);
            return;
        }
        string text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
        if (!String.IsNullOrWhiteSpace(text))
        {
            string trimmed = text.Trim();
            if (IsYouTubeUrl(trimmed))
            {
                if (String.IsNullOrWhiteSpace(urlBox.Text)) urlBox.Text = trimmed;
                else if (!urlBox.Text.Contains(trimmed)) urlBox.AppendText(Environment.NewLine + trimmed);
            }
        }
    }

    // Qualquer link http(s) é aceito: o yt-dlp reconhece YouTube, TikTok, Instagram, X/Twitter e muitos outros.
    private static bool IsYouTubeUrl(string text)
    {
        if (IsLocalMedia(text)) return true;
        Uri uri;
        return Uri.TryCreate(text, UriKind.Absolute, out uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host.Contains(".");
    }

    private static bool IsYouTubeHost(string text)
    {
        Uri uri;
        return Uri.TryCreate(text, UriKind.Absolute, out uri) &&
            (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));
    }

    private void UrlBoxTextChanged(object sender, EventArgs e)
    {
        previewTimer.Stop();
        previewRequestId++;
        downloadButton.Enabled = false;
        if (isDownloading) return;
        DisposeThumbnail();
        RefreshTrimTargets();
        List<string> links = ReadLinks();
        if (links.Count > 0) suppressBatchCountAfterReset = false;
        UpdateQueueButton(links);
        if (links.Count > 0)
        {
            SetPreviewVisible(true);
            UpdateDownloadButton();
            if (links.Exists(delegate(string link) { return !IsYouTubeUrl(link); }))
            {
                videoTitleLabel.Text = "Confira os links informados";
                thumbnail.Loading = false;
                videoDetailsLabel.Text = "Use um link válido por linha (YouTube, TikTok, Instagram, X…).";
                musicStatusLabel.Text = "Não foi possível verificar a etiqueta Música";
            }
            else
            {
                videoTitleLabel.Text = "Identificando vídeo…";
                thumbnail.Loading = true;
                autoDownloadArmed = autoDownloadCheckBox.Checked;
                videoDetailsLabel.Text = links.Count > 1 ? links.Count.ToString(CultureInfo.InvariantCulture) + " links na fila" : "Buscando título e miniatura";
                musicStatusLabel.Text = "Verificando direitos autorais…";
                previewTimer.Start();
            }
        }
        else
        {
            suppressBatchCountAfterReset = true;
            SetPreviewVisible(false);
            downloadButton.Text = "Baixar";
        }
    }

    // Começa o download sozinho quando "Baixar ao colar" está ligado e todos os links foram identificados.
    private void TryAutoDownload(List<string> links)
    {
        if (!autoDownloadArmed || !autoDownloadCheckBox.Checked || isDownloading || engineUpdating) return;
        if (links.Count == 0 || !AreAllLinksValid(links)) return;
        foreach (string link in links)
        {
            if (IsLocalMedia(link)) continue;
            QueueVideoInfo info = PeekQueueInfo(link);
            if (info == null || String.IsNullOrEmpty(info.InfoJsonPath)) return;
        }
        autoDownloadArmed = false;
        StartDownload(this, EventArgs.Empty);
    }

    private List<string> ReadLinks()
    {
        List<string> links = new List<string>();
        string[] lines = urlBox.Text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            string link = line.Trim();
            if (link.Length > 0) links.Add(link);
        }
        return links;
    }

    private bool AreAllLinksValid(List<string> links)
    {
        return links.Count > 0 && !links.Exists(delegate(string link) { return !IsYouTubeUrl(link); });
    }

    private void UpdateDownloadButton()
    {
        List<string> links = ReadLinks();
        downloadButton.Text = links.Count > 1 && !suppressBatchCountAfterReset
            ? "Baixar (" + links.Count.ToString(CultureInfo.InvariantCulture) + ")"
            : "Baixar";
        downloadButton.Enabled = !isDownloading && AreAllLinksValid(links);
    }

    private void UpdateQueueButton(List<string> links)
    {
        UpdateSizeEstimate();
        UpdateThumbnailButton();
        if (links.Count > 0 && AreAllLinksValid(links) && !AreQueueInfoCached(links))
        {
            queueButton.Text = "Preparando fila…";
            queueButton.Enabled = false;
            return;
        }
        queueButton.Text = "Fila (" + links.Count.ToString(CultureInfo.InvariantCulture) + ")";
        queueButton.Enabled = !isDownloading && links.Count > 0;
    }

    private bool AreQueueInfoCached(List<string> links)
    {
        lock (queueInfoCacheLock)
        {
            foreach (string link in links)
                if (!queueInfoCache.ContainsKey(link)) return false;
        }
        return true;
    }

    private void SetPreviewVisible(bool visible)
    {
        if (previewShown == visible) return;
        previewShown = visible;
        AnimateReveal(true, visible ? 1 : 0);
        AnimateReveal(false, visible && trimCheckBox.Checked ? 1 : 0);
    }

    // Abre/fecha a prévia ou o painel de corte deslizando o resto da janela junto (a janela cresce suavemente).
    private void AnimateReveal(bool preview, double target)
    {
        object key = preview ? previewRevealKey : trimRevealKey;
        double from = preview ? previewReveal : trimReveal;
        if (!IsHandleCreated || !Visible || (Math.Abs(from - target) < 0.001 && !Motion.IsRunning(key)))
        {
            Motion.Stop(key);
            if (preview) previewReveal = target; else trimReveal = target;
            LayoutForm();
            return;
        }
        Motion.Run(key, preview ? 460 : 420, delegate(double t)
        {
            if (IsDisposed) return;
            double value = from + (target - from) * t;
            if (preview) previewReveal = value; else trimReveal = value;
            Redraw.Suspend(this);
            try { LayoutForm(); }
            finally { Redraw.Resume(this); }
        }, null, Motion.EaseOut);
    }

    private string HistoryFilePath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine("YT Downloader", "download-history.json"));
        }
    }

    private List<DownloadHistoryEntry> LoadDownloadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return new List<DownloadHistoryEntry>();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 2 * 1024 * 1024;
            List<DownloadHistoryEntry> entries = serializer.Deserialize<List<DownloadHistoryEntry>>(File.ReadAllText(HistoryFilePath, Encoding.UTF8));
            return entries ?? new List<DownloadHistoryEntry>();
        }
        catch { return new List<DownloadHistoryEntry>(); }
    }

    private void SaveDownloadHistory(List<DownloadHistoryEntry> entries)
    {
        string directory = Path.GetDirectoryName(HistoryFilePath);
        Directory.CreateDirectory(directory);
        string temporary = HistoryFilePath + ".tmp";
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = 2 * 1024 * 1024;
        File.WriteAllText(temporary, serializer.Serialize(entries), new UTF8Encoding(false));
        if (File.Exists(HistoryFilePath)) File.Replace(temporary, HistoryFilePath, null);
        else File.Move(temporary, HistoryFilePath);
    }

    private void RecordCompletedFiles(List<string> files, DateTime since)
    {
        List<string> paths = new List<string>(files);
        paths.RemoveAll(delegate(string item) { return String.IsNullOrWhiteSpace(item) || !File.Exists(item); });
        if (paths.Count == 0)
        {
            string found = FindNewestDownloadedFile(since);
            if (found != null) paths.Add(found);
        }
        if (paths.Count == 0) return;
        List<DownloadHistoryEntry> entries = LoadDownloadHistory();
        foreach (string path in paths)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            entries.RemoveAll(delegate(DownloadHistoryEntry item)
            {
                return String.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase);
            });
            entries.Insert(0, new DownloadHistoryEntry
            {
                FilePath = path,
                Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                DownloadedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
            });
        }
        if (entries.Count > 100) entries.RemoveRange(100, entries.Count - 100);
        try { SaveDownloadHistory(entries); }
        catch { }
    }

    // Plano B: se o caminho informado pelo yt-dlp não for encontrado, procura na pasta de destino
    // o arquivo do formato escolhido que foi criado durante este download.
    private string FindNewestDownloadedFile(DateTime since)
    {
        try
        {
            if (String.IsNullOrEmpty(queueDestination) || !Directory.Exists(queueDestination)) return null;
            string extension = "." + (queueFormat ?? "").ToLowerInvariant();
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(queueDestination))
            {
                if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime created = File.GetCreationTime(file);
                DateTime written = File.GetLastWriteTime(file);
                DateTime latest = created > written ? created : written;
                if (latest < since) continue;
                if (latest > bestTime) { best = file; bestTime = latest; }
            }
            return best;
        }
        catch { return null; }
    }

    private void ShowDownloadHistory(object sender, EventArgs e)
    {
        List<DownloadHistoryEntry> entries = LoadDownloadHistory();
        Color background = Theme.Bg;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;

        Form dialog = new Form();
        Motion.FadeIn(dialog);
        Buffering.Enable(dialog);
        dialog.Text = "Downloads";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        dialog.ClientSize = new Size(560, 470);
        dialog.Font = Font;
        dialog.BackColor = background;
        dialog.ForeColor = mainText;

        Label heading = new Label();
        heading.Text = "Downloads";
        heading.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
        heading.ForeColor = mainText;
        heading.AutoSize = true;
        heading.Location = new Point(22, 18);
        dialog.Controls.Add(heading);

        Label count = new Label();
        count.Text = entries.Count.ToString(CultureInfo.InvariantCulture) + (entries.Count == 1 ? " arquivo baixado" : " arquivos baixados");
        count.ForeColor = mutedText;
        count.AutoSize = true;
        count.Location = new Point(24, 48);
        dialog.Controls.Add(count);

        RoundedButton clearButton = new RoundedButton(10);
        clearButton.Text = "Limpar histórico";
        clearButton.SetBounds(396, 22, 146, 34);
        clearButton.BackColor = surface;
        clearButton.ForeColor = mainText;
        clearButton.BorderColor = Theme.LineStrong;
        clearButton.Cursor = entries.Count > 0 ? Cursors.Hand : Cursors.Default;
        clearButton.Enabled = entries.Count > 0;
        dialog.Controls.Add(clearButton);

        FlowLayoutPanel list = new FlowLayoutPanel();
        list.SetBounds(18, 76, 524, 374);
        list.AutoScroll = true;
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.BackColor = background;
        list.Padding = new Padding(2);
        dialog.Controls.Add(list);

        clearButton.Click += delegate
        {
            if (entries.Count == 0) return;
            DialogResult confirm = MessageBox.Show(dialog,
                "Tem certeza de que deseja limpar a lista de downloads?",
                "Limpar downloads",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            entries.Clear();
            try { SaveDownloadHistory(entries); }
            catch { }

            count.Text = "0 arquivos baixados";
            clearButton.Enabled = false;
            clearButton.Cursor = Cursors.Default;

            list.SuspendLayout();
            while (list.Controls.Count > 0)
            {
                Control c = list.Controls[0];
                list.Controls.RemoveAt(0);
                c.Dispose();
            }

            Label emptyLabel = new Label();
            emptyLabel.Text = "Seus downloads aparecerão aqui.";
            emptyLabel.ForeColor = mutedText;
            emptyLabel.AutoSize = true;
            emptyLabel.Margin = new Padding(14, 26, 0, 0);
            list.Controls.Add(emptyLabel);
            list.ResumeLayout();
        };

        if (entries.Count == 0)
        {
            Label empty = new Label();
            empty.Text = "Seus downloads aparecerão aqui.";
            empty.ForeColor = mutedText;
            empty.AutoSize = true;
            empty.Margin = new Padding(14, 26, 0, 0);
            list.Controls.Add(empty);
        }
        foreach (DownloadHistoryEntry entry in entries)
        {
            RoundedPanel card = new RoundedPanel(12);
            card.SetBounds(0, 0, 492, 68);
            card.Margin = new Padding(2, 2, 2, 7);
            card.BackColor = surface;
            card.BorderColor = Theme.Line;

            string path = entry.FilePath;
            Action playAction = delegate
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(dialog, "Esse arquivo não está mais nesse local.", "Arquivo não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { MessageBox.Show(dialog, "Não foi possível abrir o arquivo.", "Downloads", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };

            Label fileName = new Label();
            fileName.Text = Path.GetFileName(entry.FilePath);
            fileName.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            fileName.ForeColor = mainText;
            fileName.AutoEllipsis = true;
            fileName.SetBounds(14, 11, 260, 23);
            fileName.Cursor = Cursors.Hand;
            fileName.DoubleClick += delegate { playAction(); };
            card.Controls.Add(fileName);

            DateTime downloadedAt;
            string dateText = DateTime.TryParse(entry.DownloadedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out downloadedAt)
                ? downloadedAt.ToLocalTime().ToString("dd/MM/yyyy · HH:mm", CultureInfo.CurrentCulture)
                : "Data indisponível";
            Label details = new Label();
            details.Text = entry.Format + "  ·  " + dateText + (File.Exists(entry.FilePath) ? "" : "  ·  arquivo não encontrado");
            details.Font = new Font("Segoe UI", 8.5F);
            details.ForeColor = mutedText;
            details.AutoEllipsis = true;
            details.SetBounds(14, 37, 260, 20);
            card.Controls.Add(details);

            card.DoubleClick += delegate { playAction(); };

            RoundedButton playButton = new RoundedButton(9);
            playButton.Text = "Abrir";
            playButton.SetBounds(282, 17, 86, 34);
            playButton.BackColor = surface;
            playButton.ForeColor = mainText;
            playButton.BorderColor = Theme.LineStrong;
            playButton.Cursor = Cursors.Hand;
            playButton.Click += delegate { playAction(); };
            card.Controls.Add(playButton);

            RoundedButton openButton = new RoundedButton(9);
            openButton.Text = "Pasta";
            openButton.SetBounds(374, 17, 104, 34);
            openButton.BackColor = surface;
            openButton.ForeColor = mainText;
            openButton.BorderColor = Theme.LineStrong;
            openButton.Cursor = Cursors.Hand;
            openButton.Click += delegate
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(dialog, "Esse arquivo não está mais nesse local.", "Arquivo não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Process.Start("explorer.exe", "/select,\"" + path.Replace("\"", "") + "\""); }
                catch { MessageBox.Show(dialog, "Não foi possível abrir a pasta do arquivo.", "Downloads", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            card.Controls.Add(openButton);
            list.Controls.Add(card);
        }

        dialog.ShowDialog(this);
        dialog.Dispose();
    }

    private void ShowQueue(object sender, EventArgs e)
    {
        List<string> links = ReadLinks();
        if (links.Count == 0) return;
        Color background = Theme.Bg;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;

        Form dialog = new Form();
        Motion.FadeIn(dialog);
        Buffering.Enable(dialog);
        dialog.Text = "Fila de downloads";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        dialog.ClientSize = new Size(520, 500);
        dialog.Font = Font;
        dialog.BackColor = background;
        dialog.ForeColor = mainText;

        Label heading = new Label();
        heading.Text = links.Count.ToString(CultureInfo.InvariantCulture) + (links.Count == 1 ? " vídeo na fila" : " vídeos na fila");
        heading.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
        heading.ForeColor = mainText;
        heading.AutoSize = true;
        heading.Location = new Point(22, 18);
        dialog.Controls.Add(heading);

        FlowLayoutPanel list = new FlowLayoutPanel();
        list.SetBounds(18, 62, 484, 354);
        list.AutoScroll = true;
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.BackColor = background;
        list.Padding = new Padding(2);
        dialog.Controls.Add(list);
        ToolTip queueTip = new ToolTip();

        Label editHint = new Label();
        editHint.Text = "Selecione um item para organizar a fila";
        editHint.ForeColor = mutedText;
        editHint.AutoSize = true;
        editHint.Location = new Point(22, 426);
        dialog.Controls.Add(editHint);

        RoundedButton upButton = new RoundedButton(9);
        upButton.Text = "↑";
        upButton.SetBounds(22, 450, 44, 32);
        upButton.Enabled = false;
        dialog.Controls.Add(upButton);
        RoundedButton downButton = new RoundedButton(9);
        downButton.Text = "↓";
        downButton.SetBounds(72, 450, 44, 32);
        downButton.Enabled = false;
        dialog.Controls.Add(downButton);
        RoundedButton removeButton = new RoundedButton(9);
        removeButton.Text = "Remover";
        removeButton.SetBounds(126, 450, 92, 32);
        removeButton.Enabled = false;
        dialog.Controls.Add(removeButton);

        List<QueueVideoRow> rows = new List<QueueVideoRow>();
        Dictionary<Control, QueueVideoRow> cardRows = new Dictionary<Control, QueueVideoRow>();
        Control selectedCard = null;
        Color selectedBorder = Theme.Accent;
        Action<Control> selectCard = delegate(Control selected)
        {
            selectedCard = selected;
            foreach (Control item in list.Controls)
            {
                RoundedPanel itemCard = item as RoundedPanel;
                if (itemCard != null)
                {
                    itemCard.BorderColor = itemCard == selectedCard
                        ? selectedBorder
                        : Theme.Line;
                    itemCard.Invalidate();
                }
            }
            upButton.Enabled = selectedCard != null && list.Controls.GetChildIndex(selectedCard) > 0;
            downButton.Enabled = selectedCard != null && list.Controls.GetChildIndex(selectedCard) < list.Controls.Count - 1;
            removeButton.Enabled = selectedCard != null;
        };
        upButton.Click += delegate
        {
            if (selectedCard == null) return;
            int index = list.Controls.GetChildIndex(selectedCard);
            if (index > 0) list.Controls.SetChildIndex(selectedCard, index - 1);
            selectCard(selectedCard);
        };
        downButton.Click += delegate
        {
            if (selectedCard == null) return;
            int index = list.Controls.GetChildIndex(selectedCard);
            if (index < list.Controls.Count - 1) list.Controls.SetChildIndex(selectedCard, index + 1);
            selectCard(selectedCard);
        };
        removeButton.Click += delegate
        {
            if (selectedCard == null) return;
            int index = list.Controls.GetChildIndex(selectedCard);
            QueueVideoRow removedRow = cardRows[selectedCard];
            if (removedRow.Thumbnail.Image != null)
            {
                removedRow.Thumbnail.Image.Dispose();
                removedRow.Thumbnail.Image = null;
            }
            rows.Remove(removedRow);
            cardRows.Remove(selectedCard);
            Control removedCard = selectedCard;
            selectedCard = null;
            list.Controls.Remove(removedCard);
            removedCard.Dispose();
            heading.Text = list.Controls.Count.ToString(CultureInfo.InvariantCulture) +
                (list.Controls.Count == 1 ? " vídeo na fila" : " vídeos na fila");
            if (list.Controls.Count > 0) selectCard(list.Controls[Math.Min(index, list.Controls.Count - 1)]);
            else selectCard(null);
        };
        for (int i = 0; i < links.Count; i++)
        {
            RoundedPanel card = new RoundedPanel(12);
            card.SetBounds(0, 0, 450, 92);
            card.Margin = new Padding(2, 2, 2, 8);
            card.BackColor = surface;
            card.Tag = links[i];
            card.BorderColor = Theme.Line;

            RoundedThumbnail rowThumbnail = new RoundedThumbnail();
            rowThumbnail.PlaceholderColor = Theme.Track;
            rowThumbnail.PlaceholderTextColor = mutedText;
            rowThumbnail.SetBounds(12, 12, 108, 68);
            card.Controls.Add(rowThumbnail);

            Label rowTitle = new Label();
            rowTitle.Text = IsYouTubeUrl(links[i]) ? "Carregando título…" : "Link inválido";
            rowTitle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            rowTitle.ForeColor = mainText;
            rowTitle.AutoEllipsis = true;
            rowTitle.SetBounds(136, 17, 294, 30);
            card.Controls.Add(rowTitle);

            Label rowDetails = new Label();
            rowDetails.Text = IsYouTubeUrl(links[i]) ? "Buscando miniatura e detalhes…" : "Insira um link válido.";
            rowDetails.Font = new Font("Segoe UI", 8.5F);
            rowDetails.ForeColor = mutedText;
            rowDetails.AutoEllipsis = true;
            rowDetails.SetBounds(138, 49, 292, 25);
            card.Controls.Add(rowDetails);

            Label rowMusicStatus = new Label();
            rowMusicStatus.Text = IsYouTubeUrl(links[i]) ? "Verificando direitos autorais…" : "";
            rowMusicStatus.Font = new Font("Segoe UI", 8F);
            rowMusicStatus.ForeColor = mutedText;
            rowMusicStatus.AutoEllipsis = true;
            rowMusicStatus.SetBounds(138, 72, 292, 16);
            card.Controls.Add(rowMusicStatus);
            queueTip.SetToolTip(rowMusicStatus, "A identificação de música não confirma direitos de uso ou permissão para monetizar.");

            list.Controls.Add(card);
            QueueVideoRow row = new QueueVideoRow { Thumbnail = rowThumbnail, Title = rowTitle, Details = rowDetails, MusicStatus = rowMusicStatus };
            rows.Add(row);
            cardRows[card] = row;
            EventHandler chooseThisCard = delegate { selectCard(card); };
            card.Click += chooseThisCard;
            foreach (Control child in card.Controls) child.Click += chooseThisCard;
            QueueVideoInfo cachedInfo = GetCachedQueueVideoInfo(links[i]);
            if (cachedInfo != null)
            {
                row.Title.Text = cachedInfo.Title;
                row.Details.Text = cachedInfo.Details;
                row.MusicStatus.Text = cachedInfo.HasMusicCredit.HasValue
                    ? (cachedInfo.HasMusicCredit.Value ? "Direitos autorais detectados" : "Livre de direitos autorais")
                    : "Não foi possível verificar a etiqueta Música";
                row.MusicStatus.ForeColor = cachedInfo.HasMusicCredit == true
                    ? Theme.Warn
                    : mutedText;
                queueTip.SetToolTip(row.MusicStatus, cachedInfo.HasMusicCredit == true
                    ? "A seção Música foi identificada na página do YouTube."
                    : "A seção Música não foi identificada na página do YouTube.");
                rowThumbnail.Image = cachedInfo.Thumbnail;
            }
        }
        if (list.Controls.Count > 0) selectCard(list.Controls[0]);

        dialog.FormClosed += delegate
        {
            List<string> orderedLinks = new List<string>();
            foreach (Control item in list.Controls)
                if (item.Tag is string) orderedLinks.Add((string)item.Tag);
            string updatedText = String.Join(Environment.NewLine, orderedLinks.ToArray());
            if (!String.Equals(urlBox.Text, updatedText, StringComparison.Ordinal)) urlBox.Text = updatedText;
            queueTip.Dispose();
            foreach (QueueVideoRow row in rows)
            {
                Image image = row.Thumbnail.Image;
                row.Thumbnail.Image = null;
                if (image != null) image.Dispose();
            }
        };
        dialog.ShowDialog(this);
        dialog.Dispose();
    }

    private QueueVideoInfo FetchQueueVideoInfo(string url)
    {
        if (IsLocalMedia(url)) return FetchLocalFileInfo(url.Trim().Trim('"'));
        QueueVideoInfo result = new QueueVideoInfo { Title = "Não foi possível identificar", Details = "Confira sua conexão e tente novamente." };
        string downloader = Program.YtDlpPath;
        if (!File.Exists(downloader))
        {
            result.Details = "Componente yt-dlp.exe não encontrado.";
            return result;
        }
        // A checagem de "Música" do YouTube roda ao mesmo tempo que o yt-dlp (antes era uma depois da outra).
        Task<bool?> musicCheck = IsYouTubeHost(url)
            ? Task.Factory.StartNew<bool?>(delegate { return DetectYoutubeMusicSection(url); })
            : null;
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = downloader;
            info.Arguments = "--dump-single-json --skip-download --no-playlist --no-warnings --socket-timeout 15 --retries 1 " + Quote(url);
            info.WorkingDirectory = AppFolder;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(info))
            {
                Task<string> stdout = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadToEnd(); });
                Task<string> stderr = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                process.WaitForExit();
                Task.WaitAll(stdout, stderr);
                if (process.ExitCode != 0 || String.IsNullOrWhiteSpace(stdout.Result))
                {
                    result.Details = CompactError(stderr.Result);
                    return result;
                }
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 20 * 1024 * 1024;
                Dictionary<string, object> metadata = serializer.Deserialize<Dictionary<string, object>>(stdout.Result);
                try
                {
                    Directory.CreateDirectory(previewDirectory);
                    string infoPath = Path.Combine(previewDirectory, PreviewKey(url) + ".info.json");
                    File.WriteAllText(infoPath, stdout.Result, new UTF8Encoding(false));
                    result.InfoJsonPath = infoPath;
                    result.FetchedAt = DateTime.Now;
                }
                catch { }
                bool? musicSection = null;
                if (musicCheck != null)
                {
                    try { musicSection = musicCheck.Wait(12000) ? musicCheck.Result : null; }
                    catch { musicSection = null; }
                }
                result.HasMusicCredit = musicSection == true || (musicSection != false && HasMusicCredit(metadata))
                    ? (bool?)true
                    : musicSection;
                result.Title = JsonString(metadata, "title");
                object durationValue;
                double durationSeconds;
                if (metadata.TryGetValue("duration", out durationValue) && durationValue != null &&
                    Double.TryParse(Convert.ToString(durationValue, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
                    result.DurationSeconds = durationSeconds;
                result.EstimatedBytes = JsonLong(metadata, "filesize_approx");
                if (result.EstimatedBytes <= 0) result.EstimatedBytes = JsonLong(metadata, "filesize");
                result.Formats = ReadFormats(metadata, result.DurationSeconds);
                string creator = JsonString(metadata, "uploader");
                if (String.IsNullOrEmpty(creator)) creator = JsonString(metadata, "channel");
                string duration = FormatDuration(metadata);
                result.Details = creator;
                if (!String.IsNullOrEmpty(duration)) result.Details = String.IsNullOrEmpty(result.Details) ? duration : result.Details + "  ·  " + duration;
                if (String.IsNullOrEmpty(result.Details)) result.Details = "Vídeo identificado";
                result.Thumbnail = DownloadThumbnail(GetThumbnailUrls(metadata));
                result.ThumbnailUrls = GetSaveThumbnailUrls(metadata);
            }
        }
        catch (Exception ex) { result.Details = CompactError(ex.Message); }
        return result;
    }

    private QueueVideoInfo GetQueueVideoInfo(string url)
    {
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo cached;
            if (queueInfoCache.TryGetValue(url, out cached)) return CopyQueueVideoInfo(cached);
        }

        QueueVideoInfo loaded = FetchQueueVideoInfo(url);
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo existing;
            if (queueInfoCache.TryGetValue(url, out existing))
            {
                if (loaded.Thumbnail != null) loaded.Thumbnail.Dispose();
                return CopyQueueVideoInfo(existing);
            }
            queueInfoCache[url] = CopyQueueVideoInfo(loaded);
        }
        return loaded;
    }

    private QueueVideoInfo GetCachedQueueVideoInfo(string url)
    {
        if (!IsYouTubeUrl(url)) return null;
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo cached;
            return queueInfoCache.TryGetValue(url, out cached) ? CopyQueueVideoInfo(cached) : null;
        }
    }

    private static QueueVideoInfo CopyQueueVideoInfo(QueueVideoInfo source)
    {
        return new QueueVideoInfo
        {
            Title = source.Title,
            Details = source.Details,
            HasMusicCredit = source.HasMusicCredit,
            EstimatedBytes = source.EstimatedBytes,
            DurationSeconds = source.DurationSeconds,
            ThumbnailUrls = source.ThumbnailUrls,
            Formats = source.Formats,
            InfoJsonPath = source.InfoJsonPath,
            FetchedAt = source.FetchedAt,
            Thumbnail = source.Thumbnail == null ? null : new Bitmap(source.Thumbnail)
        };
    }

    private static long JsonLong(Dictionary<string, object> data, string key)
    {
        object value;
        long result;
        return data.TryGetValue(key, out value) && value != null &&
            Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            ? result : 0;
    }

    private void PreviewTimerTick(object sender, EventArgs e)
    {
        previewTimer.Stop();
        List<string> links = ReadLinks();
        if (!isDownloading && AreAllLinksValid(links)) PreloadQueueInfo(links, previewRequestId);
    }

    private void PreloadQueueInfo(List<string> links, int requestId)
    {
        List<string> snapshot = new List<string>(links);
        string shown = snapshot.Count > 0 ? snapshot[snapshot.Count - 1] : null;
        // Título, canal e capa em ~0,2 s pelo atalho oficial do YouTube, enquanto o resto carrega.
        if (shown != null && QuickPreviewEndpoint(shown) != null && PeekQueueInfo(shown) == null)
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { ShowQuickPreview(shown, requestId); });
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            // Identifica até 4 links ao mesmo tempo (antes era um por vez).
            int[] order = new int[snapshot.Count];
            for (int k = 0; k < order.Length; k++) order[k] = order.Length - 1 - k;
            System.Threading.Tasks.Parallel.ForEach(order, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, delegate(int i)
            {
                if (requestId != previewRequestId) return;
                QueueVideoInfo info = GetQueueVideoInfo(snapshot[i]);
                if (i == snapshot.Count - 1)
                    ShowPreviewResult(requestId, info.Thumbnail, info.Title, info.Details, info.HasMusicCredit, info.DurationSeconds);
                else
                {
                    if (info.Thumbnail != null) info.Thumbnail.Dispose();
                    if (!IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (!IsDisposed && requestId == previewRequestId && !isDownloading) RefreshTrimTargets();
                            });
                        }
                        catch { }
                    }
                }
            });
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || requestId != previewRequestId) return;
                        UpdateQueueButton(ReadLinks());
                        if (!isDownloading) RefreshTrimTargets();
                        TryAutoDownload(snapshot);
                    });
                }
                catch { }
            }
        });
    }

    private static string JsonString(Dictionary<string, object> data, string key)
    {
        object value;
        return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
    }

    // Lista de endereços de miniatura em ordem de preferência. O Windows (GDI+) não abre WebP,
    // então damos prioridade às versões JPG e usamos as capas padrão do YouTube como reserva.
    private static List<string> GetThumbnailUrls(Dictionary<string, object> data)
    {
        List<string> urls = new List<string>();
        object value;
        if (data.TryGetValue("thumbnails", out value))
        {
            System.Collections.IList thumbnails = value as System.Collections.IList;
            if (thumbnails != null)
            {
                for (int i = thumbnails.Count - 1; i >= 0 && urls.Count < 2; i--)
                {
                    Dictionary<string, object> item = thumbnails[i] as Dictionary<string, object>;
                    if (item == null) continue;
                    string url = JsonString(item, "url");
                    string path = url.Split('?')[0];
                    bool decodable = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                    if (decodable && !urls.Contains(url)) urls.Add(url);
                }
            }
        }
        string id = JsonString(data, "id");
        bool isYouTube = JsonString(data, "extractor_key").StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);
        if (isYouTube && !String.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$"))
        {
            urls.Add("https://i.ytimg.com/vi/" + id + "/hqdefault.jpg");
            urls.Add("https://i.ytimg.com/vi/" + id + "/mqdefault.jpg");
        }
        string direct = JsonString(data, "thumbnail");
        if (!String.IsNullOrEmpty(direct) && !urls.Contains(direct)) urls.Add(direct);
        return urls;
    }

    private static string FormatDuration(Dictionary<string, object> data)
    {
        string text = JsonString(data, "duration_string");
        if (!String.IsNullOrEmpty(text)) return text;
        object value;
        double seconds;
        if (data.TryGetValue("duration", out value) && value != null && Double.TryParse(Convert.ToString(value), out seconds))
        {
            TimeSpan duration = TimeSpan.FromSeconds(seconds);
            return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }
        return "";
    }

    private static Image DownloadThumbnail(List<string> urls)
    {
        foreach (string url in urls)
        {
            Image image = DownloadThumbnail(url);
            if (image != null) return image;
        }
        return null;
    }

    private static Image DownloadThumbnail(string url)
    {
        if (String.IsNullOrEmpty(url)) return null;
        try
        {
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Headers.Add("User-Agent", "Mozilla/5.0");
                byte[] bytes = client.DownloadData(url);
                using (MemoryStream stream = new MemoryStream(bytes))
                using (Image original = Image.FromStream(stream)) return new Bitmap(original);
            }
        }
        catch { return null; }
    }

    private int fullPreviewShownFor = -1;

    // Atalhos oficiais (oEmbed) que devolvem título, canal e capa em ~0,2 s, enquanto o yt-dlp carrega o resto.
    private static string QuickPreviewEndpoint(string link)
    {
        if (IsYouTubeHost(link)) return "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(link);
        Uri uri;
        if (!Uri.TryCreate(link, UriKind.Absolute, out uri)) return null;
        string host = uri.Host.ToLowerInvariant();
        string encoded = Uri.EscapeDataString(link);
        if (host == "tiktok.com" || host.EndsWith(".tiktok.com")) return "https://www.tiktok.com/oembed?url=" + encoded;
        if (host == "vimeo.com" || host.EndsWith(".vimeo.com")) return "https://vimeo.com/api/oembed.json?url=" + encoded;
        if (host == "soundcloud.com" || host.EndsWith(".soundcloud.com")) return "https://soundcloud.com/oembed?format=json&url=" + encoded;
        if (host == "dailymotion.com" || host.EndsWith(".dailymotion.com") || host == "dai.ly") return "https://www.dailymotion.com/services/oembed?format=json&url=" + encoded;
        return null;
    }

    private static readonly Regex YouTubeIdPattern = new Regex(@"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

    private static string YouTubeId(string link)
    {
        if (!IsYouTubeHost(link)) return null;
        Match match = YouTubeIdPattern.Match(link);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void ShowQuickPreview(string link, int requestId)
    {
        try
        {
            // No YouTube a capa tem endereço fixo: baixa ao mesmo tempo que o título (aparece quase instantâneo).
            string id = YouTubeId(link);
            if (id != null)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    Image fast = DownloadThumbnail("https://i.ytimg.com/vi/" + id + "/mqdefault.jpg");
                    if (fast == null) return;
                    if (IsDisposed || !IsHandleCreated || requestId != previewRequestId) { fast.Dispose(); return; }
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed || requestId != previewRequestId || fullPreviewShownFor == requestId || thumbnail.Image != null) { fast.Dispose(); return; }
                            SetThumbnail(fast);
                        });
                    }
                    catch { fast.Dispose(); }
                });
            }
            string json;
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("User-Agent", "Mozilla/5.0");
                json = client.DownloadString(QuickPreviewEndpoint(link));
            }
            Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string title = JsonString(data, "title");
            string author = JsonString(data, "author_name");
            if (title.Length == 0 || requestId != previewRequestId) return;
            Image image = id != null ? null : DownloadThumbnail(JsonString(data, "thumbnail_url"));
            if (IsDisposed || !IsHandleCreated)
            {
                if (image != null) image.Dispose();
                return;
            }
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || requestId != previewRequestId || fullPreviewShownFor == requestId)
                {
                    if (image != null) image.Dispose();
                    return;
                }
                videoTitleLabel.Text = title;
                videoDetailsLabel.Text = author.Length > 0 ? author + "  ·  carregando detalhes…" : "Carregando detalhes…";
                Motion.FadeText(videoTitleLabel, Theme.Fg, 320);
                Motion.FadeText(videoDetailsLabel, Theme.Muted, 420);
                if (image != null)
                {
                    SetThumbnail(image);
                }
            });
        }
        catch { }
    }

    private void ShowPreviewResult(int requestId, Image image, string title, string details, bool? hasMusicCredit, double durationSeconds = 0)
    {
        if (IsDisposed || !IsHandleCreated || requestId != previewRequestId)
        {
            if (image != null) image.Dispose();
            return;
        }
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || requestId != previewRequestId)
                {
                    if (image != null) image.Dispose();
                    return;
                }
                fullPreviewShownFor = requestId;
                if (videoTitleLabel.Text != title) { videoTitleLabel.Text = title; Motion.FadeText(videoTitleLabel, Theme.Fg, 320); }
                if (videoDetailsLabel.Text != details) { videoDetailsLabel.Text = details; Motion.FadeText(videoDetailsLabel, Theme.Muted, 420); }
                if (hasMusicCredit.HasValue)
                {
                    musicStatusLabel.Text = hasMusicCredit.Value
                        ? "Direitos autorais detectados"
                        : "Livre de direitos autorais";
                    musicStatusLabel.ForeColor = hasMusicCredit.Value
                        ? Theme.Warn
                        : Theme.Muted;
                    musicStatusTip.SetToolTip(musicStatusLabel, hasMusicCredit.Value
                        ? "A seção Música foi identificada na página do YouTube."
                        : "A seção Música não foi identificada na página do YouTube.");
                }
                else
                {
                    musicStatusLabel.Text = "Não foi possível consultar os metadados musicais";
                    musicStatusLabel.ForeColor = Theme.Muted;
                    musicStatusTip.SetToolTip(musicStatusLabel, "Não foi possível obter os metadados deste vídeo. Tente novamente mais tarde.");
                }
                if (image != null) SetThumbnail(image);
                thumbnail.Loading = false;
                if (!isDownloading) RefreshTrimTargets();
                UpdateDownloadButton();
            });
        }
        catch { if (image != null) image.Dispose(); }
    }

    private bool? DetectYoutubeMusicSection(string url)
    {
        try
        {
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");
                string html = client.DownloadString(url);
                if (html.IndexOf("videoDescriptionMusicSectionRenderer", StringComparison.Ordinal) >= 0) return true;

                object initialData = ExtractInitialData(html);
                if (initialData == null) return null;
                return HasYoutubeMusicRenderer(initialData);
            }
        }
        catch { return null; }
    }

    private static object ExtractInitialData(string html)
    {
        object data = ExtractJsonAfterMarker(html, "ytInitialData");
        return data ?? ExtractJsonAfterMarker(html, "ytInitialPlayerResponse");
    }

    private static object ExtractJsonAfterMarker(string html, string markerText)
    {
        int marker = html.IndexOf(markerText, StringComparison.Ordinal);
        if (marker < 0) return null;
        int start = html.IndexOf('{', marker);
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                try
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = 40 * 1024 * 1024;
                    return serializer.DeserializeObject(html.Substring(start, i - start + 1));
                }
                catch { return null; }
            }
        }
        return null;
    }

    private static bool HasYoutubeMusicRenderer(object node)
    {
        Dictionary<string, object> dictionary = node as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (pair.Key == "videoDescriptionMusicSectionRenderer") return true;
                if (pair.Key == "horizontalCardListRenderer" && IsMusicCardList(pair.Value)) return true;
                if (HasYoutubeMusicRenderer(pair.Value)) return true;
            }
            return false;
        }
        object[] array = node as object[];
        if (array != null)
            foreach (object item in array)
                if (HasYoutubeMusicRenderer(item)) return true;
        return false;
    }

    private static bool IsMusicCardList(object rendererValue)
    {
        Dictionary<string, object> renderer = rendererValue as Dictionary<string, object>;
        if (renderer == null) return false;
        object header;
        object cards;
        if (!renderer.TryGetValue("header", out header) || !renderer.TryGetValue("cards", out cards)) return false;
        if (!ContainsMusicHeading(header)) return false;
        object[] cardArray = cards as object[];
        return cardArray != null && cardArray.Length > 0;
    }

    private static bool ContainsMusicHeading(object node)
    {
        Dictionary<string, object> dictionary = node as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if ((pair.Key == "simpleText" || pair.Key == "text") && pair.Value is string)
                {
                    string text = ((string)pair.Value).Trim();
                    if (String.Equals(text, "Music", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(text, "Música", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(text, "Music in this video", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(text, "Música neste vídeo", StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (ContainsMusicHeading(pair.Value)) return true;
            }
            return false;
        }
        object[] array = node as object[];
        if (array != null)
            foreach (object item in array)
                if (ContainsMusicHeading(item)) return true;
        return false;
    }

    private static bool HasMusicCredit(Dictionary<string, object> metadata)
    {
        if (!String.IsNullOrWhiteSpace(JsonString(metadata, "track")) ||
            !String.IsNullOrWhiteSpace(JsonString(metadata, "artist"))) return true;
        object value;
        if (metadata.TryGetValue("artists", out value))
        {
            object[] artists = value as object[];
            if (artists != null)
                foreach (object artist in artists)
                    if (artist != null && !String.IsNullOrWhiteSpace(Convert.ToString(artist))) return true;
            string singleArtist = value as string;
            if (!String.IsNullOrWhiteSpace(singleArtist)) return true;
        }
        if (metadata.TryGetValue("categories", out value))
        {
            object[] categories = value as object[];
            if (categories != null)
                foreach (object category in categories)
                    if (category != null && String.Equals(Convert.ToString(category), "Music", StringComparison.OrdinalIgnoreCase)) return true;
            string[] stringCategories = value as string[];
            if (stringCategories != null)
                foreach (string category in stringCategories)
                    if (String.Equals(category, "Music", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string CompactError(string text)
    {
        if (String.IsNullOrWhiteSpace(text)) return "Não foi possível concluir.";
        string[] lines = text.Trim().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string last = lines.Length == 0 ? text.Trim() : lines[lines.Length - 1].Trim();
        return last.Length > 150 ? last.Substring(0, 147) + "…" : last;
    }

    // Troca a miniatura com fade cruzado (a antiga é descartada quando a nova termina de aparecer).
    private void SetThumbnail(Image image)
    {
        Image old = thumbnail.Image;
        thumbnail.Loading = false;
        thumbnail.Image = image;
        if (old != null && old != image)
        {
            Timer later = new Timer();
            later.Interval = 450;
            later.Tick += delegate { later.Stop(); later.Dispose(); old.Dispose(); };
            later.Start();
        }
    }

    private void DisposeThumbnail()
    {
        Image old = thumbnail.Image;
        thumbnail.Image = null;
        if (old != null) old.Dispose();
    }

    private void BrowseFolder(object sender, EventArgs e)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.Description = "Escolha a pasta onde salvar os arquivos";
            if (Directory.Exists(folderBox.Text)) dialog.SelectedPath = folderBox.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private List<string> FetchPlaylistVideoUrls(string playlistUrl)
    {
        string downloader = Program.YtDlpPath;
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = downloader;
            info.Arguments = "--flat-playlist --print id --no-warnings " + Quote(playlistUrl);
            info.WorkingDirectory = AppFolder;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 && !String.IsNullOrWhiteSpace(output))
                {
                    List<string> results = new List<string>();
                    string[] ids = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string id in ids)
                    {
                        string cleanId = id.Trim();
                        if (cleanId.Length > 0 && !cleanId.StartsWith("[") && !cleanId.Contains(" "))
                            results.Add("https://www.youtube.com/watch?v=" + cleanId);
                    }
                    return results;
                }
            }
        }
        catch { }
        return null;
    }

    private void StartDownload(object sender, EventArgs e)
    {
        if (isDownloading)
        {
            CancelDownload();
            return;
        }
        List<JobSpec> retrySpecs = pendingRetrySpecs;
        pendingRetrySpecs = null;
        if (engineUpdating)
        {
            MessageBox.Show(this, "O motor de download está sendo atualizado. Aguarde alguns segundos e tente de novo.", "Atualizando", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        List<string> links = ReadLinks();
        if (!AreAllLinksValid(links)) return;
        if (links.Count == 1 && IsYouTubeHost(links[0]) && links[0].Contains("list="))
        {
            DialogResult playlistChoice = MessageBox.Show(this,
                "Identificamos que este link contém uma playlist.\r\n\r\nDeseja carregar todos os vídeos da playlist para a fila de downloads?\r\n(Clique em 'Não' para baixar apenas o vídeo individual)",
                "Playlist detectada",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (playlistChoice == DialogResult.Cancel) return;
            if (playlistChoice == DialogResult.Yes)
            {
                videoDetailsLabel.Text = "Carregando vídeos da playlist…";
                Refresh();
                List<string> playlistLinks = FetchPlaylistVideoUrls(links[0]);
                if (playlistLinks != null && playlistLinks.Count > 0)
                {
                    urlBox.Text = String.Join(Environment.NewLine, playlistLinks.ToArray());
                    links = playlistLinks;
                }
                else
                {
                    MessageBox.Show(this, "Não foi possível carregar a lista de vídeos da playlist. O download continuará com o link original.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                string single = Regex.Replace(links[0], @"([?&])list=[^&]+&?", "$1").TrimEnd('?', '&');
                urlBox.Text = single;
                links = new List<string> { single };
            }
        }
        string downloader = Program.YtDlpPath;
        string ffmpeg = Path.Combine(AppFolder, "ffmpeg.exe");
        if (!File.Exists(downloader) || !File.Exists(ffmpeg))
        {
            MessageBox.Show(this, "Os componentes yt-dlp.exe e ffmpeg.exe precisam estar na mesma pasta do aplicativo.", "Componentes ausentes", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string destination = folderBox.Text.Trim();
        if (destination.Length == 0)
        {
            MessageBox.Show(this, "Escolha uma pasta de destino.", "Pasta necessária", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Directory.Exists(destination))
        {
            MessageBox.Show(this, "A pasta selecionada não existe. Escolha uma pasta existente para salvar o arquivo.", "Pasta inválida", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!ConfirmAvailableSpace(destination, links)) return;

        queueFormat = formatBox.SelectedItem.ToString().ToLowerInvariant();
        List<JobSpec> specs = retrySpecs ?? BuildJobSpecs(links);
        jobSpecs.Clear();
        jobSpecs.AddRange(specs);
        downloadQueue.Clear();
        foreach (JobSpec spec in specs) downloadQueue.Add(spec.Url);
        nextJobIndex = 0;
        runningJobs.Clear();
        failedLinks.Clear();
        failedSpecs.Clear();
        queueCompleted = 0;
        queueFailed = 0;
        queueFailures.Length = 0;
        queueCancelled = false;
        queueDestination = destination;
        queueNormalize = normalizeCheckBox.Checked;
        SavePreviewVolume();
        isDownloading = true;
        previewRequestId++;
        previewTimer.Stop();
        SetInputsEnabled(false);
        downloadButton.Text = "Cancelar";
        downloadButton.Enabled = true;
        progress.Value = 0;
        percentLabel.Text = "0%";
        progress.Visible = true;
        progress.Active = true;
        percentLabel.Visible = true;
        queueProgressLabel.Text = "Preparando…";
        queueProgressLabel.Visible = true;
        queueStartedAt = DateTime.Now;
        smoothedEtaSeconds = -1;
        jobProgressTimer.Start();
        StartPendingJobs();
    }

    private bool ConfirmAvailableSpace(string destination, List<string> links)
    {
        try
        {
            string fullPath = Path.GetFullPath(destination);
            string root = Path.GetPathRoot(fullPath);
            DriveInfo drive = new DriveInfo(root);
            int estimateFiles;
            int estimateUnknown;
            long estimated = EstimateBytesFor(null, out estimateFiles, out estimateUnknown);
            long reserve = estimated > 0 ? Math.Max(256L * 1024 * 1024, estimated / 10) : 256L * 1024 * 1024;
            long required = estimated > Int64.MaxValue - reserve ? Int64.MaxValue : estimated + reserve;
            if (drive.AvailableFreeSpace >= required) return true;
            string freeText = FormatBytes(drive.AvailableFreeSpace);
            string neededText = estimated > 0 ? FormatBytes(required) : "pelo menos 256 MB";
            return MessageBox.Show(this,
                "O espaço livre parece insuficiente. Disponível: " + freeText + ". Estimativa necessária: " + neededText + ".\r\n\r\nDeseja tentar mesmo assim?",
                "Pouco espaço em disco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        catch
        {
            return MessageBox.Show(this, "Não foi possível verificar o espaço livre nesta unidade. Deseja iniciar mesmo assim?",
                "Espaço em disco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private void SetInputsEnabled(bool enabled)
    {
        urlBox.Enabled = enabled;
        typeBox.Enabled = enabled;
        typeSwitch.Enabled = enabled;
        formatBox.Enabled = enabled;
        qualityBox.Enabled = enabled;
        folderBox.Enabled = enabled;
        browseButton.Enabled = enabled;
        themeButton.Enabled = enabled;
        converterButton.Enabled = enabled;
        updateButton.Enabled = enabled && !engineUpdating;
        trimCheckBox.Enabled = enabled;
        normalizeCheckBox.Enabled = enabled;
        subtitlesCheckBox.Enabled = enabled;
        localFileButton.Enabled = enabled;
        sizeEstimateLabel.Visible = enabled && previewShown && sizeEstimateLabel.Text.Length > 0;
        UpdateThumbnailButton();
        addSegmentButton.Enabled = enabled;
        if (!enabled) clipboardButton.Visible = false;
        trimTargetBox.Enabled = enabled;
        if (enabled) LoadTrimTarget();
        else
        {
            StopPreview();
            timeRangeSlider.Enabled = false;
            trimResetButton.Enabled = false;
            trimPlayButton.Enabled = false;
            trimStartBox.Enabled = false;
            trimEndBox.Enabled = false;
        }
        if (enabled) UpdateQueueButton(ReadLinks());
        else queueButton.Enabled = false;
        downloadButton.Text = enabled ? "Baixar" : "Cancelar";
        downloadButton.Enabled = !enabled;
    }

    // ===================== Vários trechos do mesmo vídeo =====================
    // O controle sempre edita o "trecho atual" (trimRanges/trimFades). "+ Adicionar trecho" guarda o atual
    // como uma etiqueta e libera o controle para marcar outro. Cada trecho vira um arquivo separado.

    private List<TrimSegment> GetSavedSegments(string link)
    {
        List<TrimSegment> list;
        if (String.IsNullOrEmpty(link)) return null;
        return savedSegments.TryGetValue(link, out list) ? list : null;
    }

    private int SavedSegmentCount(string link)
    {
        List<TrimSegment> list = GetSavedSegments(link);
        return list == null ? 0 : list.Count;
    }

    private TrimSegment CurrentSegment(string link)
    {
        int[] range;
        double[] fades = GetFades(link);
        bool hasRange = TryGetTrimRange(link, out range);
        if (!hasRange && fades == null) return null;
        TrimSegment segment = new TrimSegment();
        segment.Start = hasRange ? range[0] : 0;
        segment.End = hasRange ? range[1] : TrimDuration(link);
        segment.Full = !hasRange;
        segment.FadeIn = fades == null ? 0 : fades[0];
        segment.FadeOut = fades == null ? 0 : fades[1];
        return segment;
    }

    private void SetCurrentSegment(string link, TrimSegment segment)
    {
        trimRanges.Remove(link);
        trimFades.Remove(link);
        if (segment == null) return;
        if (!segment.Full) trimRanges[link] = new int[] { segment.Start, segment.End };
        if (segment.FadeIn > 0.05 || segment.FadeOut > 0.05) trimFades[link] = new double[] { segment.FadeIn, segment.FadeOut };
    }

    // Trechos que viram arquivos no download: os guardados + o atual (se tiver corte ou fade).
    private List<TrimSegment> SegmentsForDownload(string link)
    {
        List<TrimSegment> result = new List<TrimSegment>();
        List<TrimSegment> saved = GetSavedSegments(link);
        if (saved != null) result.AddRange(saved);
        TrimSegment current = CurrentSegment(link);
        if (current != null) result.Add(current);
        return result;
    }

    private void AddSegment()
    {
        string link = trimTargetLink;
        if (link == null || TrimDuration(link) <= 0) return;
        TrimSegment current = CurrentSegment(link);
        if (current == null)
        {
            musicStatusTip.Show("Marque um trecho (ou um fade) primeiro e depois clique em + Adicionar trecho.", addSegmentButton, 0, -38, 2600);
            return;
        }
        List<TrimSegment> list = GetSavedSegments(link);
        if (list == null)
        {
            list = new List<TrimSegment>();
            savedSegments[link] = list;
        }
        list.Add(current);
        SetCurrentSegment(link, null);
        LoadTrimTarget();
        trimTargetBox.Invalidate();
    }

    private void EditSegment(int index)
    {
        string link = trimTargetLink;
        List<TrimSegment> list = GetSavedSegments(link);
        if (list == null || index < 0 || index >= list.Count) return;
        TrimSegment chosen = list[index];
        list.RemoveAt(index);
        TrimSegment current = CurrentSegment(link);
        if (current != null) list.Insert(index, current);
        if (list.Count == 0) savedSegments.Remove(link);
        SetCurrentSegment(link, chosen);
        LoadTrimTarget();
        trimTargetBox.Invalidate();
    }

    private void RemoveSegment(int index)
    {
        string link = trimTargetLink;
        List<TrimSegment> list = GetSavedSegments(link);
        if (list == null || index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
        if (list.Count == 0) savedSegments.Remove(link);
        LoadTrimTarget();
        trimTargetBox.Invalidate();
    }

    private static string SegmentLabel(TrimSegment segment)
    {
        string text = segment.Full ? "inteiro" : FormatTime(segment.Start) + "–" + FormatTime(segment.End);
        if (segment.FadeIn > 0.05 || segment.FadeOut > 0.05) text += " · fade";
        return text;
    }

    // Etiquetas dos trechos guardados (clique = editar, ✕ = remover).
    private void RefreshSegmentChips()
    {
        List<TrimSegment> list = GetSavedSegments(trimTargetLink);
        Color surface = Theme.AccentSoft;
        Color border = Theme.Accent;
        Color text = Theme.Fg;
        Color muted = Theme.Muted;
        trimSegmentsFlow.SuspendLayout();
        while (trimSegmentsFlow.Controls.Count > 0)
        {
            Control old = trimSegmentsFlow.Controls[0];
            trimSegmentsFlow.Controls.RemoveAt(0);
            old.Dispose();
        }
        if (list == null || list.Count == 0)
        {
            Label hint = new Label();
            hint.Text = "Quer vários cortes do mesmo vídeo? Marque um trecho e clique em + Adicionar trecho.";
            hint.Font = new Font("Segoe UI", 8F);
            hint.ForeColor = muted;
            hint.AutoSize = false;
            hint.AutoEllipsis = true;
            hint.Size = new Size(trimSegmentsFlow.Width - 4, 22);
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Margin = new Padding(0, 2, 0, 0);
            trimSegmentsFlow.Controls.Add(hint);
        }
        else
        {
            using (Font chipFont = new Font("Segoe UI", 8F, FontStyle.Bold))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    int index = i;
                    string caption = (i + 1).ToString(CultureInfo.InvariantCulture) + " · " + SegmentLabel(list[i]);
                    int textWidth = TextRenderer.MeasureText(caption, chipFont).Width;
                    RoundedPanel chip = new RoundedPanel(9);
                    chip.Size = new Size(textWidth + 30, 24);
                    chip.Margin = new Padding(0, 1, 6, 0);
                    chip.BackColor = surface;
                    chip.BorderColor = border;
                    chip.Cursor = Cursors.Hand;

                    Label label = new Label();
                    label.Text = caption;
                    label.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
                    label.ForeColor = text;
                    label.BackColor = surface;
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    label.SetBounds(8, 3, textWidth + 2, 18);
                    label.Cursor = Cursors.Hand;
                    label.Click += delegate { EditSegment(index); };
                    chip.Click += delegate { EditSegment(index); };
                    chip.Controls.Add(label);

                    Label close = new Label();
                    close.Text = "✕";
                    close.Font = new Font("Segoe UI", 7.5F);
                    close.ForeColor = muted;
                    close.BackColor = surface;
                    close.TextAlign = ContentAlignment.MiddleCenter;
                    close.SetBounds(textWidth + 10, 3, 16, 18);
                    close.Cursor = Cursors.Hand;
                    close.Click += delegate { RemoveSegment(index); };
                    chip.Controls.Add(close);

                    musicStatusTip.SetToolTip(label, "Clique para editar este trecho");
                    musicStatusTip.SetToolTip(close, "Remover este trecho");
                    trimSegmentsFlow.Controls.Add(chip);
                }
            }
        }
        trimSegmentsFlow.ResumeLayout();

        List<int[]> marks = new List<int[]>();
        if (list != null)
            foreach (TrimSegment segment in list)
                if (!segment.Full) marks.Add(new int[] { segment.Start, segment.End });
        timeRangeSlider.Marks = marks;
    }

    // ===================== Arquivos do computador =====================

    private static readonly string[] LocalMediaExtensions = new string[]
    {
        ".mp3", ".m4a", ".wav", ".flac", ".opus", ".ogg", ".aac", ".wma",
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".flv", ".3gp", ".ts"
    };

    private static bool IsLocalMedia(string text)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(text)) return false;
            string path = text.Trim().Trim('"');
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(path)) return false;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(LocalMediaExtensions, extension) < 0) return false;
            return File.Exists(path);
        }
        catch { return false; }
    }

    private void AddLinesToQueue(IEnumerable<string> lines)
    {
        List<string> existing = ReadLinks();
        StringBuilder builder = new StringBuilder(urlBox.Text.TrimEnd());
        bool changed = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim().Trim('"');
            if (line.Length == 0 || existing.Contains(line) || !IsYouTubeUrl(line)) continue;
            if (builder.Length > 0) builder.Append(Environment.NewLine);
            builder.Append(line);
            existing.Add(line);
            changed = true;
        }
        if (changed)
        {
            urlBox.Text = builder.ToString();
            urlBox.SelectionStart = urlBox.TextLength;
        }
    }

    private void BrowseLocalFiles(object sender, EventArgs e)
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = "Escolha vídeos ou músicas do seu computador";
            dialog.Multiselect = true;
            dialog.Filter = "Vídeos e músicas|*" + String.Join(";*", LocalMediaExtensions) + "|Todos os arquivos|*.*";
            if (dialog.ShowDialog(this) == DialogResult.OK) AddLinesToQueue(dialog.FileNames);
        }
    }

    private QueueVideoInfo FetchLocalFileInfo(string path)
    {
        QueueVideoInfo result = new QueueVideoInfo();
        result.Title = Path.GetFileNameWithoutExtension(path);
        double seconds = ProbeDuration(path);
        result.DurationSeconds = seconds;
        long size = 0;
        try { size = new FileInfo(path).Length; }
        catch { }
        string details = "Arquivo do computador";
        if (seconds > 0) details += "  ·  " + FormatTime((int)Math.Round(seconds));
        if (size > 0) details += "  ·  " + FormatBytes(size);
        result.Details = details;
        result.HasMusicCredit = null;
        result.Thumbnail = ExtractLocalThumbnail(path, seconds);
        result.EstimatedBytes = size;
        return result;
    }

    // Tira uma miniatura do próprio arquivo (um quadro do vídeo ou a capa da música).
    private Image ExtractLocalThumbnail(string path, double seconds)
    {
        string output = Path.Combine(previewDirectory, PreviewKey(path) + ".thumb.jpg");
        try
        {
            Directory.CreateDirectory(previewDirectory);
            double at = seconds > 6 ? Math.Min(seconds * 0.1, 30) : 0;
            string seek = at > 0 ? "-ss " + Num(at) + " " : "";
            if (!RunFfmpeg(seek + "-i \"" + path + "\" -map 0:v:0 -frames:v 1 -vf \"scale=320:-2\" \"" + output + "\"") && at > 0)
                RunFfmpeg("-i \"" + path + "\" -map 0:v:0 -frames:v 1 -vf \"scale=320:-2\" \"" + output + "\"");
            if (!File.Exists(output)) return null;
            byte[] bytes = File.ReadAllBytes(output);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image original = Image.FromStream(stream)) return new Bitmap(original);
        }
        catch { return null; }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch { }
        }
    }

    private static string AudioCodecArgs(string format, string quality)
    {
        if (format == "mp3")
        {
            string kbps = (quality ?? "").Replace(" kbps", "").Trim();
            int bitrate;
            if (!Int32.TryParse(kbps, out bitrate)) bitrate = 320;
            return "-c:a libmp3lame -b:a " + bitrate.ToString(CultureInfo.InvariantCulture) + "k -ar 44100 -id3v2_version 3";
        }
        if (format == "m4a") return "-c:a aac -b:a 256k -ar 48000";
        if (format == "flac") return "-c:a flac -ar 48000";
        if (format == "wav") return "-c:a pcm_s16le -ar 48000";
        return "-c:a libopus -b:a 160k -ar 48000";
    }

    // Converte/corta um arquivo do PC com o FFmpeg, já aplicando fade e volume igualado numa única passada.
    private void RunLocalJob(DownloadJob job, out int exitCode, out string error)
    {
        exitCode = -1;
        error = "";
        string source = job.Url.Trim().Trim('"');
        if (!File.Exists(source))
        {
            error = "O arquivo não foi encontrado: " + source;
            return;
        }
        if (String.IsNullOrEmpty(job.TempDir))
        {
            error = "Não foi possível criar a pasta temporária no destino.";
            return;
        }
        JobSpec spec = job.Spec;
        double total = ProbeDuration(source);
        double start = job.TrimRange != null ? job.TrimRange[0] : 0;
        double length = job.TrimRange != null ? job.TrimRange[1] - job.TrimRange[0] : total;
        if (length <= 0) length = total;
        job.SectionSeconds = length;

        List<string> filters = new List<string>();
        if (queueNormalize) filters.Add("loudnorm=I=-14:TP=-1.5:LRA=11");
        if (job.FadeIn > 0.05) filters.Add("afade=t=in:st=0:d=" + Num(job.FadeIn));
        if (job.FadeOut > 0.05 && length > job.FadeOut) filters.Add("afade=t=out:st=" + Num(length - job.FadeOut) + ":d=" + Num(job.FadeOut));
        string filter = filters.Count > 0 ? " -af \"" + String.Join(",", filters.ToArray()) + "\"" : "";

        string baseName = Path.GetFileNameWithoutExtension(source);
        if (job.TrimRange != null)
            baseName += " (trecho " + FormatTime(job.TrimRange[0]).Replace(':', '.') + " a " + FormatTime(job.TrimRange[1]).Replace(':', '.') + ")";
        string output = Path.Combine(job.TempDir, baseName + "." + spec.Format);
        string input = (job.TrimRange != null ? "-ss " + Num(start) + " -t " + Num(length) + " " : "") + "-i \"" + source + "\"";

        List<string> attempts = new List<string>();
        if (spec.IsVideo)
        {
            string audio = spec.Format == "webm" ? "-c:a libopus -b:a 160k" : "-c:a aac -b:a 192k";
            string encode = spec.Format == "webm"
                ? "-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 -b:v 0 -crf 32"
                : "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";
            string extra = spec.Format == "mp4" ? " -movflags +faststart" : "";
            string maps = " -map 0:v:0 -map 0:a:0?";
            // Sem corte: copia a imagem (instantâneo). Com corte, ou se a cópia não servir: recodifica.
            if (job.TrimRange == null && spec.Format != "webm") attempts.Add(input + maps + " -c:v copy" + filter + " " + audio + extra);
            attempts.Add(input + maps + " " + encode + filter + " " + audio + extra);
        }
        else
        {
            attempts.Add(input + " -map 0:a:0 -vn" + filter + " " + AudioCodecArgs(spec.Format, spec.Quality));
        }

        foreach (string attempt in attempts)
        {
            if (queueCancelled) break;
            try { if (File.Exists(output)) File.Delete(output); }
            catch { }
            using (Process process = new Process())
            {
                process.StartInfo.FileName = Path.Combine(AppFolder, "ffmpeg.exe");
                process.StartInfo.Arguments = "-nostdin -hide_banner -loglevel error -y -progress pipe:1 -nostats " + attempt + " \"" + output + "\"";
                process.StartInfo.WorkingDirectory = AppFolder;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs line) { HandleJobOutput(job, line.Data); };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs line) { HandleJobOutput(job, line.Data); };
                lock (job.Lock) job.Process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                exitCode = process.ExitCode;
                lock (job.Lock) job.Process = null;
            }
            if (exitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
            {
                lock (job.Lock) job.Saved.Add(output);
                return;
            }
        }
        if (exitCode == 0) exitCode = -3;
        lock (job.Lock) error = job.Errors.Length > 0 ? job.Errors.ToString() : "Não foi possível converter este arquivo.";
    }

    // ===================== Salvar miniatura =====================

    // Endereços da miniatura do maior para o menor (no YouTube começa pela capa em máxima resolução).
    private static List<string> GetSaveThumbnailUrls(Dictionary<string, object> data)
    {
        List<string> urls = new List<string>();
        string id = JsonString(data, "id");
        bool isYouTube = JsonString(data, "extractor_key").StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);
        if (isYouTube && !String.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$"))
        {
            urls.Add("https://i.ytimg.com/vi/" + id + "/maxresdefault.jpg");
            urls.Add("https://i.ytimg.com/vi/" + id + "/sddefault.jpg");
            urls.Add("https://i.ytimg.com/vi/" + id + "/hqdefault.jpg");
        }
        string direct = JsonString(data, "thumbnail");
        if (!String.IsNullOrEmpty(direct) && !urls.Contains(direct)) urls.Add(direct);
        object value;
        if (data.TryGetValue("thumbnails", out value))
        {
            System.Collections.IList thumbnails = value as System.Collections.IList;
            if (thumbnails != null)
            {
                for (int i = thumbnails.Count - 1; i >= 0 && urls.Count < 8; i--)
                {
                    Dictionary<string, object> item = thumbnails[i] as Dictionary<string, object>;
                    if (item == null) continue;
                    string url = JsonString(item, "url");
                    if (!String.IsNullOrEmpty(url) && !urls.Contains(url)) urls.Add(url);
                }
            }
        }
        return urls;
    }

    private void UpdateThumbnailButton()
    {
        if (savingThumbnails || thumbnailFeedbackTimer.Enabled) return;
        List<string> links = ReadLinks();
        bool ready = !isDownloading && links.Count > 0 && AreAllLinksValid(links) && AreQueueInfoCached(links);
        thumbnailSaveButton.Text = links.Count > 1 ? "↓  Miniaturas" : "↓  Miniatura";
        thumbnailSaveButton.Enabled = ready;
        thumbnailSaveButton.Invalidate();
    }

    private static string SafeFileName(string name)
    {
        string clean = String.IsNullOrWhiteSpace(name) ? "miniatura" : name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) clean = clean.Replace(invalid, '_');
        if (clean.Length > 120) clean = clean.Substring(0, 120).TrimEnd();
        return clean.TrimEnd('.', ' ');
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        string target = Path.Combine(folder, name + extension);
        int number = 2;
        while (File.Exists(target))
        {
            target = Path.Combine(folder, name + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            number++;
        }
        return target;
    }

    // Salva a miniatura de cada link (ou a capa/quadro de um arquivo do PC) na pasta de destino.
    private void SaveThumbnails()
    {
        List<string> links = ReadLinks();
        string folder = folderBox.Text.Trim();
        if (links.Count == 0) return;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "Escolha uma pasta de destino válida em \"Salvar em\".", "Miniatura", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        List<QueueVideoInfo> infos = new List<QueueVideoInfo>();
        foreach (string link in links)
        {
            QueueVideoInfo info = GetCachedQueueVideoInfo(link);
            if (info != null) infos.Add(info);
        }
        if (infos.Count == 0) return;
        savingThumbnails = true;
        thumbnailSaveButton.Enabled = false;
        thumbnailSaveButton.Text = "Salvando…";
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> saved = new List<string>();
            foreach (QueueVideoInfo info in infos)
            {
                string baseName = SafeFileName(info.Title) + " (miniatura)";
                bool done = false;
                if (info.ThumbnailUrls != null)
                {
                    foreach (string url in info.ThumbnailUrls)
                    {
                        try
                        {
                            byte[] bytes;
                            using (TimeoutWebClient client = new TimeoutWebClient())
                            {
                                client.Headers.Add("User-Agent", "Mozilla/5.0");
                                bytes = client.DownloadData(url);
                            }
                            // O YouTube devolve uma imagem cinza minúscula quando a versão máxima não existe.
                            if (bytes == null || bytes.Length < 3000) continue;
                            string path = url.Split('?')[0].ToLowerInvariant();
                            string extension = path.EndsWith(".webp") ? ".webp" : path.EndsWith(".png") ? ".png" : ".jpg";
                            string target;
                            lock (thumbnailLock)
                            {
                                target = UniquePath(folder, baseName, extension);
                                File.WriteAllBytes(target, bytes);
                            }
                            saved.Add(target);
                            done = true;
                            break;
                        }
                        catch { }
                    }
                }
                if (!done && info.Thumbnail != null)
                {
                    try
                    {
                        string target;
                        lock (thumbnailLock)
                        {
                            target = UniquePath(folder, baseName, ".jpg");
                            info.Thumbnail.Save(target, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }
                        saved.Add(target);
                    }
                    catch { }
                }
                if (info.Thumbnail != null) info.Thumbnail.Dispose();
            }
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    savingThumbnails = false;
                    if (saved.Count > 0)
                    {
                        RecordCompletedFiles(saved, DateTime.Now.AddMinutes(-1));
                        ShowToast(saved.Count == 1 ? "✓  Miniatura salva" : "✓  " + saved.Count.ToString(CultureInfo.InvariantCulture) + " miniaturas salvas",
                            saved.Count == 1 ? Path.GetFileName(saved[0]) : "Na pasta " + folder, saved[0]);
                        try
                        {
                            notifyIcon.Icon = Icon;
                            notifyIcon.Visible = true;
                            revealOnBalloonClick = saved[0];
                            notifyIcon.ShowBalloonTip(3000,
                                saved.Count == 1 ? "Miniatura salva" : saved.Count.ToString(CultureInfo.InvariantCulture) + " miniaturas salvas",
                                (saved.Count == 1 ? Path.GetFileName(saved[0]) : "Na pasta " + folder) + "\r\nClique para abrir a pasta.",
                                ToolTipIcon.Info);
                        }
                        catch { }
                    }
                    thumbnailSaveButton.Text = saved.Count == 0 ? "Não deu certo"
                        : saved.Count == 1 ? "✓  Salva" : "✓  " + saved.Count.ToString(CultureInfo.InvariantCulture) + " salvas";
                    thumbnailSaveButton.Enabled = false;
                    thumbnailFeedbackTimer.Stop();
                    thumbnailFeedbackTimer.Start();
                });
            }
            catch { }
        });
    }

    // ===================== Acabamento visual =====================

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Barra de título no tema do app (escura/clara) e cantos arredondados no Windows 11.
    private void ApplyWindowChrome(Form form)
    {
        try
        {
            if (!form.IsHandleCreated)
            {
                form.HandleCreated += delegate { ApplyWindowChrome(form); };
                return;
            }
            int dark = darkMode ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(form.Handle, 19, ref dark, 4);
            int round = 2;
            DwmSetWindowAttribute(form.Handle, 33, ref round, 4);
            // Windows 11: barra de título e borda na mesma cor do fundo do app (visual "sem moldura").
            Color caption = Theme.Bg;
            int captionColor = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(form.Handle, 35, ref captionColor, 4);
            Color line = Theme.Line;
            int borderColor = line.R | (line.G << 8) | (line.B << 16);
            DwmSetWindowAttribute(form.Handle, 34, ref borderColor, 4);
            Color fg = Theme.Fg;
            int textColor = fg.R | (fg.G << 8) | (fg.B << 16);
            DwmSetWindowAttribute(form.Handle, 36, ref textColor, 4);
        }
        catch { }
    }

    // Troca de tema com fade cruzado: uma "foto" da janela some devagar por cima das cores novas.
    private void SwitchThemeAnimated()
    {
        Form overlay = null;
        Bitmap snapshot = null;
        try
        {
            if (Visible && WindowState != FormWindowState.Minimized && Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Rectangle area = RectangleToScreen(ClientRectangle);
                snapshot = new Bitmap(area.Width, area.Height);
                using (Graphics g = Graphics.FromImage(snapshot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                overlay = new Form();
                overlay.FormBorderStyle = FormBorderStyle.None;
                overlay.ShowInTaskbar = false;
                overlay.StartPosition = FormStartPosition.Manual;
                overlay.Bounds = area;
                overlay.BackgroundImage = snapshot;
                overlay.BackgroundImageLayout = ImageLayout.None;
                overlay.Enabled = false;
                overlay.Show(this);
            }
        }
        catch
        {
            if (overlay != null) overlay.Dispose();
            overlay = null;
        }
        darkMode = !darkMode;
        ApplyTheme();
        Refresh();
        if (overlay == null)
        {
            if (snapshot != null) snapshot.Dispose();
            return;
        }
        Form fading = overlay;
        Bitmap picture = snapshot;
        Motion.Run(fading, 420, delegate(double t) { if (!fading.IsDisposed) fading.Opacity = 1 - t; },
            delegate { fading.Close(); fading.Dispose(); picture.Dispose(); Activate(); }, Motion.EaseInOut);
    }

    // ===================== Aviso rápido no app =====================

    private void ShowToast(string title, string detail, string revealPath)
    {
        bool dark = darkMode;
        Color surface = Theme.Surface;
        toastPanel.BackColor = surface;
        toastTitle.BackColor = surface;
        toastDetail.BackColor = surface;
        toastTitle.ForeColor = Theme.Fg;
        toastDetail.ForeColor = Theme.Muted;
        toastPanel.BorderColor = Theme.Line;
        toastButton.BackColor = surface;
        toastButton.ForeColor = Theme.Fg;
        toastButton.BorderColor = Theme.LineStrong;
        toastTitle.Text = title;
        toastDetail.Text = detail ?? "";
        toastRevealPath = revealPath;
        toastAction = null;
        toastButton.Text = "Abrir pasta";
        toastButton.Visible = revealPath != null;
        toastTimer.Interval = 4500;
        // Fica na faixa de baixo, à esquerda do botão Baixar. Entra subindo com efeito de mola.
        int finalTop = downloadButton.Top - 2;
        toastPanel.Elevated = true;
        toastPanel.SetBounds(28, finalTop + 26, 520, 48);
        toastPanel.Visible = true;
        toastPanel.BringToFront();
        Color titleColor = toastTitle.ForeColor;
        Color detailColor = toastDetail.ForeColor;
        toastTitle.ForeColor = surface;
        toastDetail.ForeColor = surface;
        Motion.FadeText(toastTitle, titleColor, 300);
        Motion.FadeText(toastDetail, detailColor, 380);
        Motion.Run(toastPanel, 520, delegate(double t)
        {
            if (!toastPanel.IsDisposed) toastPanel.Top = finalTop + (int)Math.Round(26 * (1 - t));
        }, null, Motion.Spring);
        brandMark.Bounce();
        toastTimer.Stop();
        toastTimer.Start();
    }

    // Aviso com um botão de ação (ex.: "Reiniciar" depois de uma atualização). Fica mais tempo na tela.
    private void ShowToastAction(string title, string detail, string buttonText, Action action)
    {
        ShowToast(title, detail, null);
        toastAction = action;
        toastButton.Text = buttonText;
        toastButton.Visible = true;
        toastTimer.Stop();
        toastTimer.Interval = 20000;
        toastTimer.Start();
    }

    // ===================== Atualização automática do app (GitHub) =====================

    private const string UpdateRepo = "DaviGerhardt/YT-Downloader";

    // Lê o latest.json do repositório; se houver versão nova, baixa o .exe e troca com segurança.
    // O .exe em uso pode ser renomeado no Windows, então a troca vale a partir da próxima abertura.
    private void CheckAppUpdate()
    {
        try
        {
            string json;
            using (TimeoutWebClient client = new TimeoutWebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("User-Agent", "YT-Downloader-by-revmpt");
                json = client.DownloadString("https://raw.githubusercontent.com/" + UpdateRepo + "/main/latest.json?t=" +
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string versionText = JsonString(data, "version").Trim();
            Version latest;
            Version current;
            if (!TryParseVersion(versionText, out latest) || !TryParseVersion(AppVersion, out current) || latest <= current) return;
            string url = JsonString(data, "url").Trim();
            if (url.Length == 0) url = "https://github.com/" + UpdateRepo + "/releases/download/v" + versionText + "/YT-Downloader.exe";
            if (!url.StartsWith("https://github.com/" + UpdateRepo + "/", StringComparison.OrdinalIgnoreCase)) return;
            string exe = Application.ExecutablePath;
            string fresh = exe + ".novo";
            string old = exe + ".antigo";
            using (WebClient client = new WebClient())
            {
                client.Headers.Add("User-Agent", "YT-Downloader-by-revmpt");
                client.DownloadFile(url, fresh);
            }
            if (new FileInfo(fresh).Length < 20L * 1024 * 1024)
            {
                File.Delete(fresh);
                return;
            }
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(fresh, exe);
            string notes = JsonString(data, "notes");
            string firstNote = notes.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length > 0
                ? notes.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0] : "Reinicie para usar a nova versão.";
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                ShowToastAction("✓  Nova versão v" + versionText + " instalada", firstNote, "Reiniciar", delegate
                {
                    try { Process.Start(Application.ExecutablePath); }
                    catch { return; }
                    Close();
                });
            });
        }
        catch { }
    }

    private void HideToast()
    {
        toastTimer.Stop();
        toastRevealPath = null;
        if (!toastPanel.Visible) return;
        // Sai descendo e desbotando.
        int startTop = toastPanel.Top;
        Color surface = toastPanel.BackColor;
        Color titleColor = toastTitle.ForeColor;
        Color detailColor = toastDetail.ForeColor;
        Motion.Run(toastPanel, 240, delegate(double t)
        {
            if (toastPanel.IsDisposed) return;
            toastPanel.Top = startTop + (int)Math.Round(22 * t);
            toastTitle.ForeColor = Motion.Mix(titleColor, surface, t);
            toastDetail.ForeColor = Motion.Mix(detailColor, surface, t);
        }, delegate { if (!toastPanel.IsDisposed) toastPanel.Visible = false; }, Motion.EaseIn);
    }

    // ===================== Link copiado =====================

    private List<string> ClipboardLinks()
    {
        List<string> found = new List<string>();
        try
        {
            if (!Clipboard.ContainsText()) return found;
            string text = Clipboard.GetText();
            if (String.IsNullOrWhiteSpace(text) || text.Length > 20000) return found;
            List<string> existing = ReadLinks();
            foreach (string raw in text.Split(new char[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = raw.Trim();
                if (!candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsYouTubeUrl(candidate) || existing.Contains(candidate) || found.Contains(candidate)) continue;
                found.Add(candidate);
            }
        }
        catch { }
        return found;
    }

    private void CheckClipboardForLinks()
    {
        List<string> links = isDownloading ? new List<string>() : ClipboardLinks();
        if (links.Count == 0)
        {
            clipboardButton.Visible = false;
            return;
        }
        clipboardButton.Text = links.Count == 1 ? "Colar link copiado" : "Colar " + links.Count.ToString(CultureInfo.InvariantCulture) + " links copiados";
        int width = TextRenderer.MeasureText(clipboardButton.Text, clipboardButton.Font).Width + 22;
        // Etiqueta discreta na linha do título "Links", alinhada ao fim do botão "＋ Arquivo".
        clipboardButton.SetBounds(localFileButton.Right - width, 62, width, 21);
        musicStatusTip.SetToolTip(clipboardButton, String.Join("\r\n", links.ToArray()));
        clipboardButton.Visible = true;
    }

    // ===================== Atalhos e bandeja =====================

    // Cria/atualiza os atalhos "YT Downloader" na área de trabalho e no menu Iniciar,
    // sempre apontando para a versão mais nova que você abriu (uma versão antiga não "rouba" o atalho).
    private void EnsureShortcuts()
    {
        try
        {
            Dictionary<string, string> settings = ReadSettings();
            string exe = Application.ExecutablePath;
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "YT Downloader.lnk");
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "YT Downloader.lnk");
            string text;
            Version current = new Version(AppVersion);
            Version stored;
            if (settings.TryGetValue("shortcut-version", out text) && TryParseVersion(text, out stored) && stored > current) return;
            bool samePath = settings.TryGetValue("shortcut-path", out text) && String.Equals(text, exe, StringComparison.OrdinalIgnoreCase);
            // Cria uma vez por versão: se você apagar o atalho, ele não volta sozinho.
            if (samePath) return;
            CreateShortcut(desktop, exe);
            CreateShortcut(startMenu, exe);
            settings["shortcut-path"] = exe;
            settings["shortcut-version"] = AppVersion;
            WriteSettings(settings);
        }
        catch { }
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        version = null;
        try
        {
            version = new Version(text);
            return true;
        }
        catch { return false; }
    }

    private static void CreateShortcut(string path, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "YT Downloader - by revmpt" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.ReleaseComObject(link);
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private void MinimizeToTray()
    {
        try
        {
            notifyIcon.Icon = Icon;
            notifyIcon.Text = "YT Downloader · baixando…";
            notifyIcon.Visible = true;
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                notifyIcon.ShowBalloonTip(3000, "Continuando em segundo plano", "O download segue aqui. Clique no ícone para voltar.", ToolTipIcon.Info);
            }
        }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private static string TrayText(string text)
    {
        return text.Length > 63 ? text.Substring(0, 62) + "…" : text;
    }

    // ===================== Conversor de arquivos (vídeo, áudio e imagem) =====================

    private static readonly string[] ConverterImageInputs = new string[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".jfif", ".avif", ".heic" };
    // Formatos de saída por tipo de arquivo. Cada arquivo só mostra os formatos do próprio tipo:
    // vídeo → formatos de vídeo, música → formatos de áudio, imagem → formatos de imagem.
    private static readonly string[] ConverterKinds = new string[] { "video", "audio", "image" };
    private static readonly string[] ConverterImageCodecs = new string[] { "mjpeg", "png", "bmp", "webp", "gif", "tiff", "jpegls", "jpeg2000", "pam", "ppm", "qoi", "targa" };
    private static readonly string[] ConverterAudioExtensions = new string[]
    {
        ".mp3", ".m4a", ".wav", ".flac", ".opus", ".ogg", ".oga", ".aac", ".wma", ".aiff", ".aif", ".alac", ".mka", ".m4b", ".amr", ".ac3", ".mp2", ".caf", ".ape", ".wv"
    };
    private static readonly string[] ConverterVideoExtensions = new string[]
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv", ".flv", ".3gp", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".ogv", ".vob", ".f4v", ".asf", ".mxf", ".divx"
    };

    private static string[] ConverterFormatsFor(string kind)
    {
        if (kind == "image") return new string[] { "PNG", "JPG", "WEBP", "BMP", "ICO" };
        if (kind == "audio") return new string[] { "MP3", "M4A", "AAC", "WAV", "FLAC", "OGG", "OPUS" };
        return new string[] { "MP4", "MKV", "MOV", "WEBM", "AVI", "GIF" };
    }

    // Monta o nome interno do destino (ex.: "MP4 · vídeo"), usado pelo montador de comandos do FFmpeg.
    private static string ConverterTarget(string kind, string format)
    {
        if (kind == "image") return format == "ICO" ? "ICO · ícone" : format + " · imagem";
        if (kind == "audio") return format + " · áudio";
        return format == "GIF" ? "GIF · animação" : format + " · vídeo";
    }

    private static string ConverterKindTitle(string kind, int count)
    {
        string n = count.ToString(CultureInfo.InvariantCulture);
        if (kind == "image") return count == 1 ? "1 imagem" : n + " imagens";
        if (kind == "audio") return count == 1 ? "1 áudio" : n + " áudios";
        return count == 1 ? "1 vídeo" : n + " vídeos";
    }

    private static string ConverterKindGlyph(string kind)
    {
        return kind == "image" ? "▣" : kind == "audio" ? "♪" : kind == "video" ? "▶" : "?";
    }

    private static string SourceFormat(ConvertItem item)
    {
        string ext = Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
        return ext == "JPEG" || ext == "JFIF" ? "JPG" : ext == "TIF" ? "TIFF" : ext;
    }

    private static bool? nvencAvailable;
    private static readonly object nvencLock = new object();

    private static string TargetExtension(string target)
    {
        return target.Split(' ')[0].ToLowerInvariant();
    }

    private static string TargetKind(string target)
    {
        if (target.Contains("áudio")) return "audio";
        if (target.Contains("imagem") || target.Contains("ícone")) return "image";
        return "video";
    }

    // Primeiro palpite pela extensão. Arquivos desconhecidos ficam "unknown" até o ffprobe olhar o conteúdo.
    private static string InputKind(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(ConverterImageInputs, extension) >= 0) return "image";
        if (Array.IndexOf(ConverterAudioExtensions, extension) >= 0) return "audio";
        if (Array.IndexOf(ConverterVideoExtensions, extension) >= 0) return "video";
        return "unknown";
    }

    // Detecta uma vez se a placa de vídeo NVIDIA pode codificar (bem mais rápido que o processador).
    private bool NvencUsable()
    {
        lock (nvencLock)
        {
            if (nvencAvailable.HasValue) return nvencAvailable.Value;
            nvencAvailable = RunFfmpeg("-f lavfi -i color=c=black:s=256x144:d=0.1 -frames:v 1 -c:v h264_nvenc -f null -");
            return nvencAvailable.Value;
        }
    }

    // Lê codecs de vídeo/áudio e a duração com o ffprobe.
    private void ProbeMedia(ConvertItem item)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(AppFolder, "ffprobe.exe");
            info.Arguments = "-v error -show_entries stream=codec_type,codec_name:format=duration -of default=noprint_wrappers=1 \"" + item.Path + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(20000);
                string codec = null;
                foreach (string raw in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("codec_name=")) codec = line.Substring(11);
                    else if (line.StartsWith("codec_type="))
                    {
                        string type = line.Substring(11);
                        if (type == "video" && item.VideoCodec == null && Array.IndexOf(ConverterImageCodecs, codec) < 0) item.VideoCodec = codec;
                        else if (type == "video" && item.VideoCodec == null) item.CoverOnly = true;
                        else if (type == "audio" && item.AudioCodec == null) item.AudioCodec = codec;
                        codec = null;
                    }
                    else if (line.StartsWith("duration="))
                    {
                        double seconds;
                        if (Double.TryParse(line.Substring(9), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) item.Duration = seconds;
                    }
                }
            }
        }
        catch { }
        item.Probed = true;
    }

    private static bool In(string value, params string[] options)
    {
        return value != null && Array.IndexOf(options, value) >= 0;
    }

    // Monta o comando do FFmpeg. Sempre que o conteúdo já é compatível, só troca o "envelope" (instantâneo, sem perda).
    private string BuildConvertArgs(ConvertItem item, string target, string output, out bool lossless)
    {
        lossless = false;
        string ext = TargetExtension(target);
        string kind = TargetKind(target);
        string input = "-i \"" + item.Path + "\" ";
        if (kind == "image")
        {
            if (ext == "png") return input + "-frames:v 1 -c:v png";
            if (ext == "jpg") return input + "-frames:v 1 -q:v 2";
            if (ext == "webp") return input + "-frames:v 1 -c:v libwebp -quality 92";
            if (ext == "bmp") return input + "-frames:v 1 -c:v bmp";
            return input + "-frames:v 1 -vf \"scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease\"";
        }
        if (kind == "audio")
        {
            string a = item.AudioCodec;
            if (ext == "mp3") { lossless = a == "mp3"; return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a libmp3lame -b:a 320k"); }
            if (ext == "aac") { lossless = a == "aac"; return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a aac -b:a 256k") + " -f adts"; }
            if (ext == "m4a") { lossless = In(a, "aac", "alac"); return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a aac -b:a 256k") + " -movflags +faststart"; }
            if (ext == "wav") return input + "-map 0:a:0 -vn -c:a pcm_s16le";
            if (ext == "flac") { lossless = a == "flac"; return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a flac"); }
            if (ext == "ogg") { lossless = a == "vorbis"; return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a libvorbis -q:a 6"); }
            lossless = a == "opus";
            return input + "-map 0:a:0 -vn " + (lossless ? "-c:a copy" : "-c:a libopus -b:a 160k");
        }
        if (ext == "gif")
            return input + "-vf \"fps=15,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer\" -loop 0";

        string v = item.VideoCodec;
        string au = item.AudioCodec;
        bool copyVideo;
        bool copyAudio;
        if (ext == "mkv") { copyVideo = true; copyAudio = true; }
        else if (ext == "mp4") { copyVideo = In(v, "h264", "hevc", "av1", "mpeg4"); copyAudio = au == null || In(au, "aac", "mp3", "ac3", "eac3", "opus", "alac", "flac"); }
        else if (ext == "mov") { copyVideo = In(v, "h264", "hevc", "mpeg4", "prores"); copyAudio = au == null || In(au, "aac", "mp3", "ac3", "alac", "pcm_s16le"); }
        else if (ext == "webm") { copyVideo = In(v, "vp8", "vp9", "av1"); copyAudio = au == null || In(au, "opus", "vorbis"); }
        else { copyVideo = In(v, "h264", "mpeg4", "mjpeg"); copyAudio = au == null || In(au, "mp3", "ac3", "pcm_s16le"); }

        string videoArgs;
        if (copyVideo) videoArgs = "-c:v copy";
        else if (ext == "webm") videoArgs = "-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 -b:v 0 -crf 32";
        else if (NvencUsable()) videoArgs = "-c:v h264_nvenc -preset p4 -cq 21 -pix_fmt yuv420p";
        else videoArgs = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";

        string audioArgs;
        if (au == null) audioArgs = "";
        else if (copyAudio) audioArgs = " -c:a copy";
        else if (ext == "webm") audioArgs = " -c:a libopus -b:a 160k";
        else if (ext == "avi") audioArgs = " -c:a libmp3lame -b:a 256k";
        else audioArgs = " -c:a aac -b:a 192k";

        lossless = copyVideo && (au == null || copyAudio);
        string maps = ext == "mkv" ? "-map 0 -c:s copy " : "-map 0:v:0 -map 0:a? ";
        string extra = ext == "mp4" || ext == "mov" ? " -movflags +faststart" : "";
        return input + maps + videoArgs + audioArgs + extra;
    }

    private static string UniqueOutputPath(string source, string extension)
    {
        string folder = Path.GetDirectoryName(source);
        string name = Path.GetFileNameWithoutExtension(source);
        if (String.Equals(Path.GetExtension(source), extension, StringComparison.OrdinalIgnoreCase)) name += " (convertido)";
        return UniquePath(folder, name, extension);
    }

    private bool RunConvert(ConvertItem item, string target)
    {
        string extension = "." + TargetExtension(target);
        string output;
        lock (thumbnailLock) output = UniqueOutputPath(item.Path, extension);
        bool lossless;
        string args = BuildConvertArgs(item, target, output, out lossless);
        item.Lossless = lossless;
        string temporary = output + ".parte" + extension;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (item.Cancelled) return false;
            using (Process process = new Process())
            {
                process.StartInfo.FileName = Path.Combine(AppFolder, "ffmpeg.exe");
                process.StartInfo.Arguments = "-nostdin -hide_banner -loglevel error -y -progress pipe:1 -nostats " + args + " \"" + temporary + "\"";
                process.StartInfo.WorkingDirectory = AppFolder;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                StringBuilder errors = new StringBuilder();
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs line)
                {
                    if (line.Data == null || item.Duration <= 0) return;
                    if (line.Data.StartsWith("out_time_us=") || line.Data.StartsWith("out_time_ms="))
                    {
                        long micro;
                        if (Int64.TryParse(line.Data.Substring(line.Data.IndexOf('=') + 1), out micro) && micro > 0)
                            item.Percent = Math.Min(99.0, micro / 1000000.0 / item.Duration * 100.0);
                    }
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs line)
                {
                    if (line.Data != null) lock (errors) if (errors.Length < 4000) errors.AppendLine(line.Data);
                };
                process.Start();
                lock (item) item.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                lock (item) item.Process = null;
                if (process.ExitCode == 0 && File.Exists(temporary) && new FileInfo(temporary).Length > 0)
                {
                    try
                    {
                        if (File.Exists(output)) File.Delete(output);
                        File.Move(temporary, output);
                        item.Output = output;
                        return true;
                    }
                    catch (Exception ex) { item.Error = ex.Message; return false; }
                }
                lock (errors) item.Error = CompactError(errors.ToString());
            }
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
            // Se a placa de vídeo falhar, tenta de novo pelo processador.
            if (attempt == 0 && args.Contains("h264_nvenc"))
            {
                args = args.Replace("-c:v h264_nvenc -preset p4 -cq 21", "-c:v libx264 -preset veryfast -crf 20");
                continue;
            }
            break;
        }
        return false;
    }

    private bool IsCompatible(ConvertItem item, string target)
    {
        string kind = TargetKind(target);
        if (item.Kind != kind) return false;
        if (kind == "video") return item.VideoCodec != null;
        if (kind == "audio") return item.AudioCodec != null;
        return true;
    }

    // Depois do ffprobe, decide o tipo pelo conteúdo de verdade (ex.: um .mp4 só com som vira "áudio",
    // um arquivo com extensão estranha que é uma foto vira "imagem").
    private static void ClassifyConvertItem(ConvertItem item)
    {
        if (item.Kind == "image") return;
        if (item.VideoCodec != null) item.Kind = "video";
        else if (item.AudioCodec != null) item.Kind = "audio";
        else if (item.CoverOnly) item.Kind = "image";
        else item.Kind = "none";
    }

    // Miniatura do card: a própria imagem, um quadro do vídeo ou a capa da música.
    private Bitmap MakeConvertThumbnail(ConvertItem item)
    {
        if (item.Kind == "none" || (item.Kind == "audio" && !item.CoverOnly)) return null;
        string extension = Path.GetExtension(item.Path).ToLowerInvariant();
        if (item.Kind == "image" && (extension == ".jpg" || extension == ".jpeg" || extension == ".jfif" || extension == ".png" ||
            extension == ".bmp" || extension == ".gif" || extension == ".tif" || extension == ".tiff" || extension == ".ico"))
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(item.Path)))
                using (Image original = Image.FromStream(stream))
                    return (Bitmap)MakeSmallThumbnail(original, 160);
            }
            catch { }
        }
        string temp = Path.Combine(Path.GetTempPath(), "ytd-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            string input = "-i \"" + item.Path + "\"";
            string tail = " -an -map 0:v:0 -frames:v 1 -vf \"scale=160:-2\" \"" + temp + "\"";
            bool ok = item.Kind == "video"
                ? RunFfmpeg("-ss 1 " + input + tail) || RunFfmpeg(input + tail)
                : RunFfmpeg(input + tail);
            if (!ok || !File.Exists(temp)) return null;
            using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(temp)))
            using (Image image = Image.FromStream(stream))
                return new Bitmap(image);
        }
        catch { return null; }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }
        }
    }

    private void ShowConverter(object sender, EventArgs e)
    {
        Color background = Theme.Bg;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;
        Color faintText = Theme.Faint;
        Color danger = Theme.Danger;
        Color okColor = Theme.Ok;
        const int W = 640;
        const int CardHeight = 72;

        Form dialog = new Form();
        dialog.Text = "Converter arquivos";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        dialog.ShowInTaskbar = false;
        dialog.ClientSize = new Size(W, 420);
        dialog.Font = Font;
        dialog.BackColor = background;
        dialog.ForeColor = mainText;
        dialog.AllowDrop = true;
        dialog.KeyPreview = true;
        Buffering.Enable(dialog);
        ApplyWindowChrome(dialog);
        Motion.FadeIn(dialog);

        Label heading = new Label();
        heading.Text = "Converter arquivos";
        heading.Font = Theme.Semibold(15F);
        heading.AutoSize = true;
        heading.Location = new Point(22, 18);
        dialog.Controls.Add(heading);

        Label subtitle = new Label();
        subtitle.Text = "Cada arquivo mostra só os formatos que fazem sentido para ele.";
        subtitle.ForeColor = mutedText;
        subtitle.AutoSize = true;
        subtitle.Location = new Point(24, 50);
        dialog.Controls.Add(subtitle);

        DropZone dropZone = new DropZone();
        dropZone.Title = "Arraste vídeos, músicas ou imagens para cá";
        dropZone.Hint = "ou clique para escolher no seu PC";
        dropZone.Font = Font;
        dialog.Controls.Add(dropZone);

        FlowLayoutPanel list = new FlowLayoutPanel();
        list.AutoScroll = true;
        list.FlowDirection = FlowDirection.TopDown;
        list.WrapContents = false;
        list.BackColor = background;
        list.Padding = new Padding(0, 2, 0, 2);
        list.AllowDrop = true;
        list.Visible = false;
        Buffering.Enable(list);
        dialog.Controls.Add(list);

        RoundedButton addButton = new RoundedButton(10);
        addButton.Text = "+  Adicionar arquivos";
        addButton.BackColor = surface;
        addButton.ForeColor = mainText;
        addButton.BorderColor = Theme.LineStrong;
        dialog.Controls.Add(addButton);

        RoundedButton clearButton = new RoundedButton(10);
        clearButton.Text = "Limpar lista";
        clearButton.Look = ButtonLook.Ghost;
        clearButton.BackColor = background;
        clearButton.ForeColor = mutedText;
        clearButton.HoverForeColor = mainText;
        dialog.Controls.Add(clearButton);

        Label targetLabel = new Label();
        targetLabel.Text = "CONVERTER PARA";
        targetLabel.Font = Theme.Semibold(7.75F);
        targetLabel.ForeColor = faintText;
        targetLabel.AutoSize = true;
        targetLabel.Visible = false;
        dialog.Controls.Add(targetLabel);

        // Uma linha por tipo presente na lista, cada uma só com os formatos daquele tipo.
        Dictionary<string, Label> kindLabels = new Dictionary<string, Label>();
        Dictionary<string, SegmentedControl> kindSwitches = new Dictionary<string, SegmentedControl>();
        Dictionary<string, string> savedSettings = ReadSettings();
        foreach (string kind in ConverterKinds)
        {
            Label label = new Label();
            label.Font = Theme.Semibold(9.5F);
            label.ForeColor = mainText;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Visible = false;
            dialog.Controls.Add(label);
            kindLabels[kind] = label;

            SegmentedControl formats = new SegmentedControl();
            formats.Items = ConverterFormatsFor(kind);
            formats.Font = Theme.Semibold(8.75F);
            formats.AccentPill = true;
            formats.TrackColor = Theme.Dark ? Theme.Sunken : Theme.Track;
            formats.TextColor = mutedText;
            formats.SelectedTextColor = Theme.OnAccent;
            formats.Visible = false;
            dialog.Controls.Add(formats);
            kindSwitches[kind] = formats;
        }

        Label status = new Label();
        status.Text = "Os arquivos convertidos ficam na mesma pasta dos originais.";
        status.ForeColor = mutedText;
        status.AutoEllipsis = true;
        status.TextAlign = ContentAlignment.MiddleLeft;
        dialog.Controls.Add(status);

        RoundedButton convertButton = new RoundedButton(12);
        convertButton.Text = "Converter";
        convertButton.Font = Theme.Semibold(10F);
        convertButton.BackColor = Theme.Accent;
        convertButton.ForeColor = Theme.OnAccent;
        convertButton.Look = ButtonLook.Primary;
        convertButton.Enabled = false;
        dialog.Controls.Add(convertButton);

        List<ConvertItem> items = new List<ConvertItem>();
        List<Image> thumbnails = new List<Image>();
        Dictionary<ConvertItem, RoundedPanel> cards = new Dictionary<ConvertItem, RoundedPanel>();
        Dictionary<ConvertItem, RoundedThumbnail> itemThumbs = new Dictionary<ConvertItem, RoundedThumbnail>();
        Dictionary<ConvertItem, Label> itemInfo = new Dictionary<ConvertItem, Label>();
        HashSet<string> initializedKinds = new HashSet<string>();
        bool running = false;
        bool closing = false;
        bool adjusting = false;
        Timer uiTimer = new Timer();
        uiTimer.Interval = 200;
        object layoutKey = new object();
        double shownListHeight = 0;
        string shownSignature = null;

        Func<ConvertItem, string> formatOf = delegate(ConvertItem item)
        {
            SegmentedControl formats;
            if (!kindSwitches.TryGetValue(item.Kind, out formats) || formats.SelectedItem == null) return null;
            return formats.SelectedItem;
        };

        // Posiciona tudo a partir da altura (animada) da lista. Devolve a altura total da janela.
        Func<double, List<string>, int> place = delegate(double listHeight, List<string> present)
        {
            int listH = (int)Math.Round(listHeight);
            int top = 82;
            bool hasItems = items.Count > 0;
            dropZone.Visible = !hasItems;
            list.Visible = hasItems;
            if (hasItems) list.SetBounds(16, top, W - 32, Math.Max(1, listH));
            else dropZone.SetBounds(22, top, W - 44, Math.Max(1, listH));
            int y = top + listH + 12;
            addButton.SetBounds(22, y, 170, 36);
            clearButton.SetBounds(198, y, 110, 36);
            clearButton.Visible = hasItems;
            y += 52;
            targetLabel.Visible = present.Count > 0;
            targetLabel.Location = new Point(24, y);
            if (present.Count > 0) y += 22;
            foreach (string kind in ConverterKinds)
            {
                bool show = present.Contains(kind);
                kindLabels[kind].Visible = show;
                kindSwitches[kind].Visible = show;
                if (!show) continue;
                kindLabels[kind].SetBounds(22, y, 150, 36);
                kindSwitches[kind].SetBounds(178, y, W - 200, 36);
                y += 44;
            }
            y += present.Count > 0 ? 6 : 0;
            status.SetBounds(22, y, W - 200, 44);
            convertButton.SetBounds(W - 172, y, 152, 48);
            return y + 64;
        };

        Action relayout = delegate
        {
            List<string> present = new List<string>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (ConvertItem item in items)
            {
                int count;
                counts.TryGetValue(item.Kind, out count);
                counts[item.Kind] = count + 1;
            }
            foreach (string kind in ConverterKinds)
            {
                if (!counts.ContainsKey(kind)) continue;
                present.Add(kind);
                kindLabels[kind].Text = ConverterKindGlyph(kind) + "   " + ConverterKindTitle(kind, counts[kind]);
            }
            double targetList = items.Count == 0 ? 190 : Math.Min(4, items.Count) * (CardHeight + 8) + 6;
            string signature = String.Join(",", present.ToArray()) + "|" + targetList.ToString(CultureInfo.InvariantCulture);
            if (signature == shownSignature) return;
            shownSignature = signature;
            double from = shownListHeight;
            if (!dialog.Visible || from == 0)
            {
                shownListHeight = targetList;
                dialog.ClientSize = new Size(W, place(targetList, present));
                return;
            }
            Motion.Run(layoutKey, 380, delegate(double t)
            {
                if (dialog.IsDisposed) return;
                shownListHeight = from + (targetList - from) * t;
                Redraw.Suspend(dialog);
                int height;
                try { height = place(shownListHeight, present); }
                finally { Redraw.Resume(dialog); }
                dialog.ClientSize = new Size(W, height);
            }, null, Motion.EaseOut);
        };

        // Escolhe o formato inicial de cada tipo: o último usado, mas nunca o mesmo formato dos arquivos.
        Action initializeKinds = delegate
        {
            foreach (string kind in ConverterKinds)
            {
                if (initializedKinds.Contains(kind)) continue;
                ConvertItem first = items.Find(delegate(ConvertItem i) { return i.Kind == kind; });
                if (first == null) continue;
                initializedKinds.Add(kind);
                SegmentedControl formats = kindSwitches[kind];
                string source = SourceFormat(first);
                string saved;
                int choice = -1;
                if (savedSettings.TryGetValue("convert-format-" + kind, out saved) && saved != source) choice = Array.IndexOf(formats.Items, saved);
                if (choice < 0)
                    for (int i = 0; i < formats.Items.Length; i++)
                        if (formats.Items[i] != source) { choice = i; break; }
                adjusting = true;
                formats.SelectedIndex = Math.Max(0, choice);
                adjusting = false;
            }
        };

        Action refreshCards = delegate
        {
            initializeKinds();
            relayout();
            int ready = 0;
            foreach (ConvertItem item in items)
            {
                string text;
                Color color = mutedText;
                string format = formatOf(item);
                if (item.Done && item.Output != null)
                {
                    text = item.Lossless ? "✓  Pronto · instantâneo, sem perder qualidade" : "✓  Pronto · " + Path.GetFileName(item.Output);
                    color = okColor;
                }
                else if (item.Done) { text = "✗  Não deu certo" + (String.IsNullOrEmpty(item.Error) ? "" : " · " + item.Error); color = danger; }
                else if (item.Running) text = item.Duration > 0 ? "Convertendo…  " + ((int)item.Percent).ToString(CultureInfo.InvariantCulture) + "%" : "Convertendo…";
                else if (!item.Probed) text = "Analisando o arquivo…";
                else if (item.Kind == "none") { text = "Não reconheci este arquivo como vídeo, música ou imagem"; color = danger; }
                else if (format == null) text = "Escolha o formato abaixo";
                else if (format == SourceFormat(item)) { text = "Já está em " + format + " · escolha outro formato abaixo"; color = Theme.Warn; }
                else if (!IsCompatible(item, ConverterTarget(item.Kind, format))) { text = "Não dá para converter este arquivo para " + format; color = danger; }
                else { text = "Vai virar  " + format; ready++; }
                if (item.StatusLabel.Text != text) item.StatusLabel.Text = text;
                item.StatusLabel.ForeColor = color;
                item.Bar.Visible = item.Running || (item.Done && item.Output != null && item.Bar.Value > 0);
                item.Bar.Active = item.Running;
                item.Bar.Value = (int)item.Percent;
                Label info;
                if (itemInfo.TryGetValue(item, out info))
                {
                    List<string> parts = new List<string>();
                    if (item.Probed && item.Duration > 0 && item.Kind != "image") parts.Add(FormatTime((int)Math.Round(item.Duration)));
                    string source = SourceFormat(item);
                    if (item.VideoCodec != null && item.Kind == "video" && item.VideoCodec.ToUpperInvariant() != source) parts.Add(item.VideoCodec.ToUpperInvariant());
                    if (item.AudioCodec != null && item.Kind != "image" && item.AudioCodec.ToUpperInvariant() != source) parts.Add(item.AudioCodec.ToUpperInvariant());
                    string details = String.Join(" · ", parts.ToArray());
                    string line = (String.IsNullOrEmpty(details) ? "" : details + "  ·  ") + info.Tag;
                    if (info.Text != line) info.Text = line;
                }
                RoundedThumbnail thumb;
                if (itemThumbs.TryGetValue(item, out thumb) && thumb.Image == null && item.Probed && !thumb.Loading)
                    thumb.PlaceholderText = ConverterKindGlyph(item.Kind);
            }
            convertButton.Enabled = !running && ready > 0;
            convertButton.Text = running ? "Convertendo…" : (ready > 1 ? "Converter (" + ready.ToString(CultureInfo.InvariantCulture) + ")" : "Converter");
            addButton.Enabled = !running;
            clearButton.Enabled = !running && items.Count > 0;
            foreach (SegmentedControl formats in kindSwitches.Values) formats.Enabled = !running;
        };

        Action<ConvertItem> removeItem = null;
        removeItem = delegate(ConvertItem item)
        {
            if (running || !items.Contains(item)) return;
            items.Remove(item);
            RoundedPanel card = cards[item];
            cards.Remove(item);
            itemThumbs.Remove(item);
            itemInfo.Remove(item);
            int startHeight = card.Height;
            // O card encolhe e some antes de sair da lista.
            Motion.Run(card, 220, delegate(double t)
            {
                if (!card.IsDisposed) card.Height = Math.Max(1, (int)Math.Round(startHeight * (1 - t)));
            }, delegate
            {
                list.Controls.Remove(card);
                card.Dispose();
                bool stillUsed = false;
                foreach (ConvertItem other in items) if (other.Kind == item.Kind) stillUsed = true;
                if (!stillUsed) initializedKinds.Remove(item.Kind);
                refreshCards();
            }, Motion.EaseIn);
        };

        Action<IEnumerable<string>> addFiles = delegate(IEnumerable<string> paths)
        {
            int stagger = 0;
            foreach (string raw in paths)
            {
                string path = raw.Trim().Trim('"');
                if (!File.Exists(path)) continue;
                bool exists = false;
                foreach (ConvertItem known in items) if (String.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase)) exists = true;
                if (exists) continue;

                ConvertItem item = new ConvertItem();
                item.Path = path;
                item.Kind = InputKind(path);
                long size = 0;
                try { size = new FileInfo(path).Length; }
                catch { }

                int cardWidth = W - 32 - 22;
                RoundedPanel card = new RoundedPanel(12);
                card.SetBounds(0, 0, cardWidth, 1);
                card.Margin = new Padding(0, 0, 0, 8);
                card.BackColor = surface;
                card.BorderColor = Theme.Line;
                card.Elevated = true;

                RoundedThumbnail thumb = new RoundedThumbnail();
                thumb.SetBounds(12, 11, 80, 48);
                thumb.Radius = 8;
                thumb.PlaceholderColor = Theme.Dark ? Theme.Raised : Theme.Sunken;
                thumb.PlaceholderTextColor = Theme.Accent;
                thumb.PlaceholderFontSize = 14F;
                thumb.Loading = true;
                card.Controls.Add(thumb);
                itemThumbs[item] = thumb;

                Label name = new Label();
                name.Text = Path.GetFileName(path);
                name.Font = Theme.Semibold(9.5F);
                name.ForeColor = mainText;
                name.AutoEllipsis = true;
                name.SetBounds(104, 11, cardWidth - 104 - 44, 20);
                card.Controls.Add(name);

                Label line = new Label();
                line.Font = new Font("Segoe UI", 8.75F);
                line.AutoEllipsis = true;
                line.SetBounds(104, 32, cardWidth - 104 - 16, 18);
                card.Controls.Add(line);
                item.StatusLabel = line;

                Label info = new Label();
                info.Tag = SourceFormat(item) + "  ·  " + FormatSize(size);
                info.Text = (string)info.Tag;
                info.Font = new Font("Segoe UI", 8F);
                info.ForeColor = faintText;
                info.AutoEllipsis = true;
                info.SetBounds(104, 50, cardWidth - 104 - 16, 16);
                card.Controls.Add(info);
                itemInfo[item] = info;

                RoundedButton remove = new RoundedButton(8);
                remove.Text = "✕";
                remove.Look = ButtonLook.Ghost;
                remove.BackColor = surface;
                remove.ForeColor = faintText;
                remove.HoverForeColor = danger;
                remove.Font = new Font("Segoe UI", 8.5F);
                remove.SetBounds(cardWidth - 38, 8, 28, 26);
                ConvertItem removable = item;
                remove.Click += delegate { removeItem(removable); };
                musicStatusTip.SetToolTip(remove, "Tirar da lista");
                card.Controls.Add(remove);

                PercentBar bar = new PercentBar();
                bar.SetBounds(104, 52, cardWidth - 104 - 16, 4);
                bar.TrackColor = Theme.Track;
                bar.FillColor = Theme.Accent;
                bar.Visible = false;
                card.Controls.Add(bar);
                item.Bar = bar;

                list.Controls.Add(card);
                items.Add(item);
                cards[item] = card;

                // O card "abre" na lista, um depois do outro (como no ForeverNote).
                RoundedPanel growing = card;
                int delay = stagger * 45;
                stagger++;
                Timer startLater = new Timer();
                startLater.Interval = Math.Max(1, delay);
                startLater.Tick += delegate
                {
                    startLater.Stop();
                    startLater.Dispose();
                    Motion.Run(growing, 420, delegate(double t)
                    {
                        if (!growing.IsDisposed) growing.Height = Math.Max(1, (int)Math.Round(CardHeight * t));
                    }, null, Motion.Spring);
                };
                startLater.Start();

                ConvertItem probeTarget = item;
                if (item.Kind == "image") item.Probed = true;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    if (probeTarget.Kind != "image")
                    {
                        ProbeMedia(probeTarget);
                        ClassifyConvertItem(probeTarget);
                    }
                    if (closing) return;
                    try { dialog.BeginInvoke((MethodInvoker)delegate { if (!dialog.IsDisposed && !closing) refreshCards(); }); }
                    catch { }
                    Bitmap picture = MakeConvertThumbnail(probeTarget);
                    try
                    {
                        dialog.BeginInvoke((MethodInvoker)delegate
                        {
                            RoundedThumbnail target;
                            if (dialog.IsDisposed || closing || !itemThumbs.TryGetValue(probeTarget, out target) || target.IsDisposed)
                            {
                                if (picture != null) picture.Dispose();
                                return;
                            }
                            target.Loading = false;
                            target.PlaceholderText = ConverterKindGlyph(probeTarget.Kind);
                            if (picture != null)
                            {
                                thumbnails.Add(picture);
                                target.Image = picture;
                            }
                            target.Invalidate();
                        });
                    }
                    catch { if (picture != null) picture.Dispose(); }
                });
            }
            refreshCards();
        };

        Action pickFiles = delegate
        {
            if (running) return;
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Escolha os arquivos para converter";
                picker.Multiselect = true;
                List<string> all = new List<string>();
                all.AddRange(ConverterVideoExtensions);
                all.AddRange(ConverterAudioExtensions);
                all.AddRange(ConverterImageInputs);
                picker.Filter = "Vídeos, músicas e imagens|*" + String.Join(";*", all.ToArray()) +
                    "|Vídeos|*" + String.Join(";*", ConverterVideoExtensions) +
                    "|Músicas e áudios|*" + String.Join(";*", ConverterAudioExtensions) +
                    "|Imagens|*" + String.Join(";*", ConverterImageInputs) + "|Todos os arquivos|*.*";
                if (picker.ShowDialog(dialog) == DialogResult.OK) addFiles(picker.FileNames);
            }
        };

        DragEventHandler dragEnter = delegate(object s, DragEventArgs args)
        {
            bool accept = !running && args.Data.GetDataPresent(DataFormats.FileDrop);
            args.Effect = accept ? DragDropEffects.Copy : DragDropEffects.None;
            if (accept) dropZone.Highlighted = true;
        };
        EventHandler dragLeave = delegate { dropZone.Highlighted = false; };
        DragEventHandler dragDrop = delegate(object s, DragEventArgs args)
        {
            dropZone.Highlighted = false;
            string[] files = args.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && !running) addFiles(files);
        };
        foreach (Control target in new Control[] { dialog, list, dropZone })
        {
            target.AllowDrop = true;
            target.DragEnter += dragEnter;
            target.DragLeave += dragLeave;
            target.DragDrop += dragDrop;
        }
        dropZone.Click += delegate { pickFiles(); };
        addButton.Click += delegate { pickFiles(); };
        clearButton.Click += delegate
        {
            if (running) return;
            items.Clear();
            cards.Clear();
            itemThumbs.Clear();
            itemInfo.Clear();
            initializedKinds.Clear();
            while (list.Controls.Count > 0)
            {
                Control old = list.Controls[0];
                list.Controls.RemoveAt(0);
                old.Dispose();
            }
            foreach (Image picture in thumbnails) picture.Dispose();
            thumbnails.Clear();
            status.Text = "Os arquivos convertidos ficam na mesma pasta dos originais.";
            status.ForeColor = mutedText;
            refreshCards();
        };
        foreach (KeyValuePair<string, SegmentedControl> pair in kindSwitches)
        {
            string kind = pair.Key;
            SegmentedControl formats = pair.Value;
            formats.SelectedIndexChanged += delegate
            {
                if (!adjusting && formats.SelectedItem != null)
                {
                    savedSettings["convert-format-" + kind] = formats.SelectedItem;
                    // Trocar o formato "zera" os itens já convertidos daquele tipo, para converter de novo se quiser.
                    foreach (ConvertItem item in items)
                        if (item.Kind == kind && item.Done) { item.Done = false; item.Output = null; item.Error = null; item.Percent = 0; }
                }
                refreshCards();
            };
        }

        convertButton.Click += delegate
        {
            List<ConvertItem> work = new List<ConvertItem>();
            Dictionary<ConvertItem, string> targets = new Dictionary<ConvertItem, string>();
            foreach (ConvertItem item in items)
            {
                string format = formatOf(item);
                if (item.Done || item.Running || !item.Probed || format == null || format == SourceFormat(item)) continue;
                string target = ConverterTarget(item.Kind, format);
                if (!IsCompatible(item, target)) continue;
                work.Add(item);
                targets[item] = target;
            }
            if (work.Count == 0) return;
            Dictionary<string, string> settings = ReadSettings();
            foreach (string kind in ConverterKinds)
                if (kindSwitches[kind].Visible && kindSwitches[kind].SelectedItem != null) settings["convert-format-" + kind] = kindSwitches[kind].SelectedItem;
            WriteSettings(settings);
            running = true;
            DateTime started = DateTime.Now;
            status.Text = "Convertendo " + work.Count.ToString(CultureInfo.InvariantCulture) + (work.Count == 1 ? " arquivo…" : " arquivos…");
            status.ForeColor = mutedText;
            uiTimer.Start();
            refreshCards();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                // Até 2 conversões ao mesmo tempo (a cópia sem perda é instantânea; a recodificação usa a placa de vídeo quando dá).
                System.Threading.Tasks.Parallel.ForEach(work, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 }, delegate(ConvertItem item)
                {
                    if (closing) return;
                    item.Running = true;
                    item.Error = null;
                    bool ok = false;
                    try { ok = RunConvert(item, targets[item]); }
                    catch (Exception ex) { item.Error = ex.Message; }
                    item.Percent = ok ? 100 : 0;
                    item.Running = false;
                    item.Done = true;
                });
                if (closing || dialog.IsDisposed) return;
                try
                {
                    dialog.BeginInvoke((MethodInvoker)delegate
                    {
                        running = false;
                        uiTimer.Stop();
                        int okCount = 0;
                        string first = null;
                        foreach (ConvertItem item in work)
                            if (item.Output != null) { okCount++; if (first == null) first = item.Output; }
                        double seconds = (DateTime.Now - started).TotalSeconds;
                        status.Text = okCount.ToString(CultureInfo.InvariantCulture) + " de " + work.Count.ToString(CultureInfo.InvariantCulture) +
                            " convertidos em " + (seconds < 1 ? "menos de 1 s" : FormatTime((int)Math.Ceiling(seconds)));
                        status.ForeColor = okCount == work.Count ? okColor : mutedText;
                        Motion.FadeText(status, status.ForeColor, 320);
                        refreshCards();
                        if (okCount > 0)
                        {
                            List<string> outputs = new List<string>();
                            foreach (ConvertItem item in work) if (item.Output != null) outputs.Add(item.Output);
                            RecordCompletedFiles(outputs, DateTime.Now.AddMinutes(-5));
                            ShowToast(okCount == 1 ? "✓  Conversão concluída" : "✓  " + okCount.ToString(CultureInfo.InvariantCulture) + " arquivos convertidos",
                                Path.GetFileName(first), first);
                        }
                    });
                }
                catch { }
            });
        };

        uiTimer.Tick += delegate { refreshCards(); };
        dialog.KeyDown += delegate(object s, KeyEventArgs args)
        {
            if (args.KeyCode == Keys.Escape && !running) dialog.Close();
            else if (args.KeyCode == Keys.V && args.Control && !running && Clipboard.ContainsFileDropList())
            {
                List<string> pasted = new List<string>();
                foreach (string file in Clipboard.GetFileDropList()) pasted.Add(file);
                addFiles(pasted);
            }
        };
        dialog.FormClosing += delegate(object s, FormClosingEventArgs args)
        {
            if (!running) return;
            if (MessageBox.Show(dialog, "Ainda há conversões em andamento. Deseja cancelar e fechar?", "Converter",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                args.Cancel = true;
                return;
            }
            closing = true;
            foreach (ConvertItem item in items)
            {
                item.Cancelled = true;
                Process process;
                lock (item) process = item.Process;
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch { }
                }
            }
        };
        refreshCards();
        dialog.ShowDialog(this);
        closing = true;
        uiTimer.Stop();
        uiTimer.Dispose();
        dialog.Dispose();
        foreach (Image picture in thumbnails) picture.Dispose();
    }

    // ===================== Fila de downloads (até 3 ao mesmo tempo) =====================

    // Cada link vira um ou mais itens: um por trecho marcado, e em "Vídeo + Áudio" também a versão MP3.
    private List<JobSpec> BuildJobSpecs(List<string> links)
    {
        List<JobSpec> specs = new List<JobSpec>();
        int type = typeBox.SelectedIndex;
        string format = formatBox.SelectedItem.ToString().ToLowerInvariant();
        string quality = qualityBox.SelectedItem == null ? "" : qualityBox.SelectedItem.ToString();
        foreach (string link in links)
        {
            List<TrimSegment> segments = trimCheckBox.Checked ? SegmentsForDownload(link) : new List<TrimSegment>();
            if (segments.Count == 0) segments.Add(null);
            foreach (TrimSegment segment in segments)
            {
                JobSpec spec = new JobSpec();
                spec.Url = link;
                spec.IsLocal = IsLocalMedia(link);
                if (segment != null)
                {
                    if (!segment.Full) spec.Range = new int[] { segment.Start, segment.End };
                    spec.FadeIn = segment.FadeIn;
                    spec.FadeOut = segment.FadeOut;
                }
                spec.IsVideo = type >= 1;
                spec.Format = format;
                spec.Quality = quality;
                spec.Subtitles = spec.IsVideo && subtitlesCheckBox.Checked;
                specs.Add(spec);
                if (type == 2)
                {
                    JobSpec audio = new JobSpec();
                    audio.Url = spec.Url;
                    audio.IsLocal = spec.IsLocal;
                    audio.Range = spec.Range;
                    audio.FadeIn = spec.FadeIn;
                    audio.FadeOut = spec.FadeOut;
                    audio.IsVideo = false;
                    audio.Format = "mp3";
                    audio.Quality = "320 kbps";
                    specs.Add(audio);
                }
            }
        }
        return specs;
    }

    private void StartPendingJobs()
    {
        while (!queueCancelled && runningJobs.Count < MaxParallelDownloads && nextJobIndex < downloadQueue.Count)
        {
            StartJob(nextJobIndex);
            nextJobIndex++;
        }
    }

    private string BuildDownloadArgs(DownloadJob job)
    {
        bool isTrimmed = job.TrimRange != null;
        string nameTemplate = isTrimmed
            ? "%(title)s (trecho " + FormatTime(job.TrimRange[0]).Replace(':', '.') + " a " + FormatTime(job.TrimRange[1]).Replace(':', '.') + ").%(ext)s"
            : "%(title)s.%(ext)s";
        string args = "--newline --no-colors --progress --force-overwrites --no-continue --progress-delta 0.3 --progress-template " +
            Quote("download:YT_PROGRESS|%(info.format_id)s|%(progress._percent_str)s|%(progress.speed)s|%(progress.eta)s") +
            " --print " + Quote("after_move:YT_SAVED:%(filepath)s") +
            " --concurrent-fragments 8 --no-playlist --no-mtime --trim-filenames 150 --retries 5 --fragment-retries 10" +
            " --socket-timeout 20 --ffmpeg-location " + Quote(AppFolder) +
            " -P " + Quote(job.TempDir ?? queueDestination) + " -o " + Quote(nameTemplate);
        if (isTrimmed)
        {
            string startStr = FormatSecondsForYtDlp(job.TrimRange[0]);
            string endStr = FormatSecondsForYtDlp(job.TrimRange[1]);
            args += " --download-sections " + Quote("*" + startStr + "-" + endStr);
            // Corte preciso de imagem exige recodificar; no áudio não é necessário (mais rápido).
            if (job.Spec.IsVideo) args += " --force-keyframes-at-cuts";
            // Reconecta sozinho se a conexão do trecho cair, em vez de ficar parado.
            args += " --downloader-args " + Quote("ffmpeg_i:-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5");
        }
        if (!job.Spec.IsVideo)
        {
            string bitrate = job.Spec.Format == "mp3" ? job.Spec.Quality.Replace(" kbps", "K") : "0";
            // Baixa só a faixa de áudio (nunca o vídeo inteiro) antes de converter.
            string audioPick = job.Spec.Format == "m4a" ? "ba[ext=m4a]/ba/b" : job.Spec.Format == "opus" ? "ba[acodec=opus]/ba/b" : "ba/b";
            args += " -f " + Quote(audioPick) + " -x --audio-format " + job.Spec.Format + " --audio-quality " + bitrate + " --embed-thumbnail --embed-metadata";
        }
        else
        {
            if (job.Spec.Quality != "Melhor disponível")
            {
                string height = job.Spec.Quality.Replace("4K (2160p)", "2160").Replace("2K (1440p)", "1440").Replace("p", "");
                args += " -f " + Quote("bestvideo[height<=" + height + "]+bestaudio/best[height<=" + height + "]/best");
            }
            args += " --remux-video " + job.Spec.Format;
            if (job.Spec.Subtitles)
                args += " --write-subs --sub-langs " + Quote("pt.*,en.*") + " --embed-subs";
        }
        // Com a identificação guardada, o download começa direto (sem consultar o site de novo).
        if (!String.IsNullOrEmpty(job.InfoJson)) return args + " --load-info-json " + Quote(job.InfoJson);
        return args + " " + Quote(job.Url);
    }

    private void StartJob(int index)
    {
        DownloadJob job = new DownloadJob();
        job.Index = index;
        job.Spec = jobSpecs[index];
        job.Url = job.Spec.Url;
        job.StartedAt = DateTime.Now.AddSeconds(-2);
        // Cada item baixa numa pasta temporária própria e depois é movido para o destino com um nome livre.
        job.TempDir = Path.Combine(queueDestination, ".ytdownloader-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            DirectoryInfo tempInfo = Directory.CreateDirectory(job.TempDir);
            tempInfo.Attributes |= FileAttributes.Hidden;
        }
        catch { job.TempDir = null; }
        job.TrimRange = job.Spec.Range;
        job.FadeIn = job.Spec.FadeIn;
        job.FadeOut = job.Spec.FadeOut;
        QueueVideoInfo known = job.Spec.IsLocal ? null : PeekQueueInfo(job.Url);
        if (known != null && !String.IsNullOrEmpty(known.InfoJsonPath) && File.Exists(known.InfoJsonPath) &&
            (DateTime.Now - known.FetchedAt).TotalHours < 3)
            job.InfoJson = known.InfoJsonPath;
        job.Args = job.Spec.IsLocal ? "" : BuildDownloadArgs(job);
        job.LastActivity = DateTime.Now;
        job.LastBytesAt = DateTime.Now;
        job.Title = ShortTitle(job.Url);
        lock (queueInfoCacheLock)
        {
            QueueVideoInfo info;
            if (queueInfoCache.TryGetValue(job.Url, out info))
            {
                if (!String.IsNullOrEmpty(info.Title)) job.Title = info.Title;
                long expected = info.EstimatedBytes;
                if (job.TrimRange != null && info.DurationSeconds > 0)
                {
                    double fraction = (job.TrimRange[1] - job.TrimRange[0]) / info.DurationSeconds;
                    expected = (long)(expected * Math.Max(0.01, Math.Min(1.0, fraction)));
                }
                job.ExpectedBytes = expected;
            }
        }
        if (job.TrimRange != null) job.SectionSeconds = job.TrimRange[1] - job.TrimRange[0];
        runningJobs.Add(job);
        string downloader = Program.YtDlpPath;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunJob(job, downloader); });
    }

    private void RunJob(DownloadJob job, string downloader)
    {
        int exitCode = -1;
        string error = "";
        if (job.Spec.IsLocal)
        {
            try { RunLocalJob(job, out exitCode, out error); }
            catch (Exception ex) { error = ex.Message; }
            if (job.TimedOut)
            {
                exitCode = exitCode == 0 ? -2 : exitCode;
                error = "Ficou mais de 2 minutos sem nenhum progresso e foi interrompido. Tente novamente.";
            }
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { FinishJob(job, exitCode, error); }); }
            catch { }
            return;
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
        try
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = downloader;
                process.StartInfo.Arguments = job.Args;
                process.StartInfo.WorkingDirectory = AppFolder;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                process.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
                process.OutputDataReceived += delegate(object source, DataReceivedEventArgs line) { HandleJobOutput(job, line.Data); };
                process.ErrorDataReceived += delegate(object source, DataReceivedEventArgs line) { HandleJobOutput(job, line.Data); };
                lock (job.Lock) job.Process = process;
                process.Start();
                if (queueCancelled)
                {
                    try { process.Kill(); }
                    catch { }
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                exitCode = process.ExitCode;
                lock (job.Lock) job.Process = null;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            lock (job.Lock) job.Process = null;
        }
        if (exitCode == 0 || String.IsNullOrEmpty(job.InfoJson) || queueCancelled || job.TimedOut) break;
        // A identificação guardada pode ter expirado: tenta de novo consultando o site.
        job.InfoJson = null;
        job.Args = BuildDownloadArgs(job);
        lock (job.Lock)
        {
            job.Errors.Length = 0;
            job.Stages.Clear();
            job.Percent = 0;
            job.Saved.Clear();
        }
        job.LastActivity = DateTime.Now;
        }
        lock (job.Lock) if (exitCode != 0 && job.Errors.Length > 0) error = job.Errors.ToString();
        if (job.TimedOut)
        {
            exitCode = exitCode == 0 ? -2 : exitCode;
            error = "Ficou mais de 2 minutos sem nenhum progresso e foi interrompido. Tente novamente.";
        }

        if (exitCode == 0 && !queueCancelled && NeedsPostProcessing(job))
        {
            job.Processing = true;
            try { PostProcessJob(job); }
            catch { }
            job.Processing = false;
        }

        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((MethodInvoker)delegate { FinishJob(job, exitCode, error); }); }
        catch { }
    }

    private void HandleJobOutput(DownloadJob job, string line)
    {
        if (String.IsNullOrWhiteSpace(line)) return;
        job.LastActivity = DateTime.Now;
        if (job.SectionSeconds > 0)
        {
            Match time = Regex.Match(line, @"time=\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
            if (time.Success)
            {
                double seconds = Int32.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                    Int32.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                    Double.Parse(time.Groups[3].Value, CultureInfo.InvariantCulture);
                lock (job.Lock)
                {
                    double percent = Math.Min(97.0, seconds / job.SectionSeconds * 97.0);
                    if (percent > job.Percent) job.Percent = percent;
                }
                return;
            }
        }
        const string savedFilePrefix = "YT_SAVED:";
        if (line.StartsWith(savedFilePrefix, StringComparison.Ordinal))
        {
            string savedPath = line.Substring(savedFilePrefix.Length).Trim();
            if (savedPath.Length > 0) lock (job.Lock) job.Saved.Add(savedPath);
            return;
        }
        if (job.Spec != null && job.Spec.IsLocal && line.IndexOf('=') > 0 && line.IndexOf(' ') < 0) return;
        int marker = line.IndexOf("YT_PROGRESS|", StringComparison.Ordinal);
        if (marker >= 0)
        {
            string[] parts = line.Substring(marker).Split('|');
            if (parts.Length < 5) return;
            double filePercent;
            string percentText = parts[2].Trim().TrimEnd('%').Trim();
            if (!Double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out filePercent)) return;
            double speed;
            double eta;
            bool hasSpeed = Double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
            bool hasEta = Double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out eta);
            lock (job.Lock)
            {
                string fileId = parts[1].Trim();
                int stage;
                if (!job.Stages.TryGetValue(fileId, out stage))
                {
                    stage = job.Stages.Count;
                    job.Stages[fileId] = stage;
                }
                double overall = job.Spec.IsVideo
                    ? (stage == 0 ? filePercent * 0.90 : 90.0 + Math.Min(1.0, filePercent / 100.0) * 9.0)
                    : filePercent * 0.99;
                if (overall > job.Percent) job.Percent = Math.Min(99.0, overall);
                job.SpeedBytes = hasSpeed && speed > 0 ? speed : 0;
                job.EtaSeconds = hasEta && eta >= 0 ? (int)eta : -1;
            }
            return;
        }
        lock (job.Lock)
        {
            if (job.Errors.Length > 12000) job.Errors.Remove(0, job.Errors.Length - 8000);
            job.Errors.AppendLine(line);
        }
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        try
        {
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;
            foreach (string file in Directory.GetFiles(directory))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return total;
    }

    private static string ShortName(string title)
    {
        if (String.IsNullOrEmpty(title)) return "item";
        return title.Length > 34 ? title.Substring(0, 33).TrimEnd() + "…" : title;
    }

    // Atualiza barra, velocidade, tempo restante e status (4x por segundo) e encerra itens travados.
    private void UpdateJobProgressUi()
    {
        if (!isDownloading) return;
        int total = downloadQueue.Count;
        if (total == 0) return;
        DateTime now = DateTime.Now;
        double sum = (queueCompleted + queueFailed) * 100.0;
        double speed = 0;
        int eta = -1;
        int processing = 0;
        string singleStatus = null;
        foreach (DownloadJob job in runningJobs)
        {
            // Mede o arquivo crescendo na pasta temporária: dá progresso mesmo quando o yt-dlp não informa (trechos).
            long bytes = DirectorySize(job.TempDir);
            double elapsed = (now - job.LastBytesAt).TotalSeconds;
            if (bytes != job.LastBytes)
            {
                if (bytes > job.LastBytes && elapsed > 0)
                {
                    double instant = (bytes - job.LastBytes) / elapsed;
                    job.MeasuredSpeed = job.MeasuredSpeed <= 0 ? instant : job.MeasuredSpeed * 0.7 + instant * 0.3;
                }
                job.LastBytes = bytes;
                job.LastBytesAt = now;
                job.LastActivity = now;
            }
            else if (elapsed > 3) job.MeasuredSpeed = 0;

            double percent;
            double jobSpeed;
            int jobEta;
            lock (job.Lock)
            {
                if (job.TrimRange != null && job.ExpectedBytes > 0 && bytes > 0)
                {
                    double estimate = Math.Min(95.0, (double)bytes / job.ExpectedBytes * 95.0);
                    if (estimate > job.Percent) job.Percent = estimate;
                }
                percent = job.Processing ? 99.0 : job.Percent;
                jobSpeed = job.SpeedBytes > 0 ? job.SpeedBytes : job.MeasuredSpeed;
                jobEta = job.EtaSeconds;
            }
            sum += percent;
            speed += jobSpeed;
            if (jobEta > eta) eta = jobEta;
            if (job.Processing) processing++;

            string state;
            if (job.Processing) state = "aplicando ajustes de áudio…";
            else if (percent >= 97) state = "finalizando…";
            else if (job.TrimRange != null) state = bytes > 0 ? "baixando trecho · " + FormatBytes(bytes) : "baixando trecho…";
            else state = "baixando…";
            singleStatus = ShortName(job.Title) + " · " + state;

            // Proteção contra travamento: 2 minutos sem nenhum sinal de vida encerra só este item.
            if (!job.Processing && !job.TimedOut && (now - job.LastActivity).TotalSeconds > 120)
            {
                Process stuck;
                lock (job.Lock) stuck = job.Process;
                if (stuck != null)
                {
                    job.TimedOut = true;
                    try { if (!stuck.HasExited) stuck.Kill(); }
                    catch { }
                }
            }
        }
        int value = Math.Max(0, Math.Min(99, (int)Math.Floor(sum / total)));
        if (value > progress.Value)
        {
            progress.Value = value;
            percentLabel.Text = value.ToString(CultureInfo.InvariantCulture) + "%";
        }
        List<string> parts = new List<string>();
        int done = queueCompleted + queueFailed;
        if (total > 1) parts.Add(done.ToString(CultureInfo.InvariantCulture) + " de " + total.ToString(CultureInfo.InvariantCulture) + " concluídos");
        if (runningJobs.Count == 1 && singleStatus != null) parts.Add(singleStatus);
        else if (runningJobs.Count > 1) parts.Add(runningJobs.Count.ToString(CultureInfo.InvariantCulture) + " baixando juntos");
        if (speed > 0) parts.Add(FormatBytes((long)speed) + "/s");
        // Tempo restante da fila inteira: ritmo real até agora (suavizado); no último lote, usa também o do yt-dlp.
        double overall = sum / total;
        double elapsedSeconds = (now - queueStartedAt).TotalSeconds;
        if (overall >= 1.5 && elapsedSeconds >= 3)
        {
            double estimate = elapsedSeconds * (100.0 - overall) / overall;
            if (eta >= 0 && nextJobIndex >= total && processing == 0) estimate = Math.Min(estimate, eta + 2);
            smoothedEtaSeconds = smoothedEtaSeconds < 0 ? estimate : smoothedEtaSeconds * 0.85 + estimate * 0.15;
            parts.Add("faltam " + (smoothedEtaSeconds < 5 ? "poucos segundos" : "≈ " + FormatTime((int)Math.Ceiling(smoothedEtaSeconds))));
        }
        else parts.Add("calculando tempo restante…");
        if (processing > 0 && runningJobs.Count > 1) parts.Add("aplicando ajustes de áudio…");
        queueProgressLabel.Text = parts.Count > 0 ? String.Join("  ·  ", parts.ToArray()) : "Preparando…";
        queueProgressLabel.Visible = true;
        if (!Visible && notifyIcon.Visible)
        {
            try
            {
                notifyIcon.Text = TrayText("YT Downloader · " + progress.Value.ToString(CultureInfo.InvariantCulture) + "%" +
                    (smoothedEtaSeconds >= 0 ? " · faltam ≈ " + FormatTime((int)Math.Ceiling(smoothedEtaSeconds)) : ""));
            }
            catch { }
        }
    }

    private void CancelDownload()
    {
        if (!isDownloading) return;
        queueCancelled = true;
        videoDetailsLabel.Text = "Cancelando download…";
        downloadButton.Enabled = false;
        foreach (DownloadJob job in runningJobs.ToArray())
        {
            Process process;
            lock (job.Lock) process = job.Process;
            if (process == null) continue;
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        }
        if (runningJobs.Count == 0) FinishCancelled();
    }

    // Ao fechar: encerra os downloads em andamento e apaga as pastas temporárias que der.
    private void StopAllJobsForExit()
    {
        if (runningJobs.Count == 0) return;
        queueCancelled = true;
        jobProgressTimer.Stop();
        foreach (DownloadJob job in runningJobs.ToArray())
        {
            Process process;
            lock (job.Lock) process = job.Process;
            if (process != null)
            {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                catch { }
            }
            try { if (!String.IsNullOrEmpty(job.TempDir) && Directory.Exists(job.TempDir)) Directory.Delete(job.TempDir, true); }
            catch { }
        }
        runningJobs.Clear();
    }

    private void FinishJob(DownloadJob job, int exitCode, string error)
    {
        if (!runningJobs.Contains(job)) return;
        runningJobs.Remove(job);
        bool success = exitCode == 0 && !queueCancelled;
        List<string> moved = MoveJobFiles(job, success);
        if (!queueCancelled)
        {
            if (exitCode == 0)
            {
                queueCompleted++;
                RecordCompletedFiles(moved, job.StartedAt);
            }
            else
            {
                queueFailed++;
                failedLinks.Add(job.Url);
                failedSpecs.Add(job.Spec);
                if (queueFailures.Length < 3000)
                    queueFailures.AppendLine("• " + ShortTitle(job.Url) + ": " + CompactError(error));
            }
        }
        if (queueCancelled)
        {
            if (runningJobs.Count == 0) FinishCancelled();
            return;
        }
        StartPendingJobs();
        UpdateJobProgressUi();
        if (runningJobs.Count == 0 && nextJobIndex >= downloadQueue.Count) FinishQueue();
    }

    // Move o(s) arquivo(s) prontos da pasta temporária para o destino, sem sobrescrever nada.
    private List<string> MoveJobFiles(DownloadJob job, bool success)
    {
        List<string> moved = new List<string>();
        string tempDir = job.TempDir;
        if (String.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
        {
            if (success)
            {
                lock (job.Lock)
                    foreach (string saved in job.Saved) if (File.Exists(saved)) moved.Add(saved);
            }
            return moved;
        }
        if (success)
        {
            try
            {
                string extension = "." + (job.Spec.Format ?? "").ToLowerInvariant();
                List<string> candidates = new List<string>();
                foreach (string file in Directory.GetFiles(tempDir))
                    if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) candidates.Add(file);
                if (candidates.Count == 0)
                {
                    foreach (string file in Directory.GetFiles(tempDir))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".part" && ext != ".ytdl" && ext != ".jpg" && ext != ".webp" && ext != ".png") candidates.Add(file);
                    }
                }
                foreach (string file in candidates)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file);
                    string target = Path.Combine(queueDestination, name + ext);
                    int number = 2;
                    while (File.Exists(target))
                    {
                        target = Path.Combine(queueDestination, name + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + ext);
                        number++;
                    }
                    File.Move(file, target);
                    moved.Add(target);
                }
            }
            catch { }
        }
        try { Directory.Delete(tempDir, true); }
        catch
        {
            // O processo cancelado pode segurar o arquivo por um instante; tenta de novo logo depois.
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                System.Threading.Thread.Sleep(1500);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            });
        }
        return moved;
    }

    private void FinishCancelled()
    {
        jobProgressTimer.Stop();
        isDownloading = false;
        SetInputsEnabled(true);
        queueCancelled = false;
        progress.Value = 0;
        percentLabel.Text = "0%";
        progress.Visible = false;
        progress.Active = false;
        percentLabel.Visible = false;
        queueProgressLabel.Visible = false;
        videoDetailsLabel.Text = queueCompleted > 0
            ? "Download cancelado · " + queueCompleted.ToString(CultureInfo.InvariantCulture) + " concluídos"
            : "Download cancelado";
        UpdateDownloadButton();
    }

    private void FinishQueue()
    {
        jobProgressTimer.Stop();
        isDownloading = false;
        suppressBatchCountAfterReset = true;
        SetInputsEnabled(true);
        progress.Visible = false;
        progress.Active = false;
        percentLabel.Visible = false;
        queueProgressLabel.Visible = false;
        videoDetailsLabel.Text = queueFailed == 0
            ? (queueCompleted == 1 ? "Download concluído" : queueCompleted.ToString(CultureInfo.InvariantCulture) + " downloads concluídos")
            : queueCompleted.ToString(CultureInfo.InvariantCulture) + " concluídos · " + queueFailed.ToString(CultureInfo.InvariantCulture) + " falharam";
        UpdateDownloadButton();
        if (Icon != null)
        {
            try
            {
                notifyIcon.Icon = Icon;
                notifyIcon.Visible = true;
                string notifTitle = queueFailed == 0 ? "Download concluído!" : "Fila de downloads concluída";
                string notifBody = queueFailed == 0
                    ? (queueCompleted == 1 ? "O arquivo foi baixado com sucesso." : queueCompleted.ToString(CultureInfo.InvariantCulture) + " downloads concluídos com sucesso.")
                    : queueCompleted.ToString(CultureInfo.InvariantCulture) + " baixados, " + queueFailed.ToString(CultureInfo.InvariantCulture) + " falharam.";
                revealOnBalloonClick = null;
                notifyIcon.ShowBalloonTip(3500, notifTitle, notifBody, queueFailed == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch { }
        }
        bool retry = false;
        List<string> retryLinks = new List<string>();
        foreach (string failed in failedLinks) if (!retryLinks.Contains(failed)) retryLinks.Add(failed);
        List<JobSpec> retrySpecs = new List<JobSpec>(failedSpecs);
        bool quietSuccess = !Visible && queueFailed == 0;
        if (!Visible && queueFailed > 0) RestoreFromTray();
        if (queueFailed > 0)
        {
            string summary = queueCompleted.ToString(CultureInfo.InvariantCulture) + " baixados; " + queueFailed.ToString(CultureInfo.InvariantCulture) + " falharam.\r\n\r\n" + queueFailures.ToString();
            if (summary.Length > 3800) summary = summary.Substring(0, 3797) + "…";
            retry = AskRetryFailures(summary, queueFailed);
        }
        else if (!quietSuccess)
        {
            MessageBox.Show(this, "Download concluído.", "Download concluído", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        suppressBatchCountAfterReset = true;
        if (!retry)
        {
            trimRanges.Clear();
            trimFades.Clear();
            savedSegments.Clear();
        }
        urlBox.Text = String.Empty;
        downloadQueue.Clear();
        nextJobIndex = 0;
        failedLinks.Clear();
        failedSpecs.Clear();
        jobSpecs.Clear();
        queueCompleted = 0;
        queueFailed = 0;
        queueFailures.Length = 0;
        progress.Value = 0;
        percentLabel.Text = "0%";
        SetPreviewVisible(false);
        DisposeThumbnail();
        videoTitleLabel.Text = "Cole um link para identificar o vídeo";
        videoDetailsLabel.Text = "A miniatura e os detalhes aparecerão aqui.";
        musicStatusLabel.Text = "Aguardando verificação musical";
        musicStatusLabel.ForeColor = Theme.Muted;
        queueButton.Text = "Fila (0)";
        queueButton.Enabled = false;
        progress.Visible = false;
        progress.Active = false;
        percentLabel.Visible = false;
        queueProgressLabel.Visible = false;
        UpdateDownloadButton();
        downloadButton.Text = "Baixar";
        downloadButton.Enabled = false;

        if (retry && retryLinks.Count > 0)
        {
            suppressBatchCountAfterReset = false;
            urlBox.Text = String.Join(Environment.NewLine, retryLinks.ToArray());
            pendingRetrySpecs = retrySpecs;
            StartDownload(this, EventArgs.Empty);
        }
    }

    // Janela no estilo do app: mostra o que falhou e oferece "Tentar novamente".
    private bool AskRetryFailures(string summary, int failedCount)
    {
        Color background = Theme.Bg;
        Color surface = Theme.Surface;
        Color mainText = Theme.Fg;
        Color mutedText = Theme.Muted;
        Color border = Theme.LineStrong;
        bool result = false;
        using (Form dialog = new Form())
        {
            Motion.FadeIn(dialog);
            Buffering.Enable(dialog);
            dialog.Text = "Fila concluída";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.ClientSize = new Size(500, 330);
            dialog.Font = Font;
            dialog.BackColor = background;
            dialog.ForeColor = mainText;

            Label heading = new Label();
            heading.Text = failedCount == 1 ? "1 download falhou" : failedCount.ToString(CultureInfo.InvariantCulture) + " downloads falharam";
            heading.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            heading.ForeColor = mainText;
            heading.AutoSize = true;
            heading.Location = new Point(22, 18);
            dialog.Controls.Add(heading);

            Label hint = new Label();
            hint.Text = "Você pode tentar baixar novamente só os itens que falharam.";
            hint.ForeColor = mutedText;
            hint.AutoSize = true;
            hint.Location = new Point(24, 52);
            dialog.Controls.Add(hint);

            RoundedPanel detailsSurface = new RoundedPanel(12);
            detailsSurface.SetBounds(20, 80, 460, 180);
            detailsSurface.BackColor = surface;
            detailsSurface.BorderColor = border;
            TextBox details = new TextBox();
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = surface;
            details.ForeColor = mainText;
            details.Font = new Font("Segoe UI", 8.5F);
            details.Text = summary;
            details.SetBounds(12, 10, 436, 160);
            detailsSurface.Controls.Add(details);
            dialog.Controls.Add(detailsSurface);

            RoundedButton closeButton = new RoundedButton(10);
            closeButton.Text = "Fechar";
            closeButton.SetBounds(232, 276, 110, 38);
            closeButton.BackColor = surface;
            closeButton.ForeColor = mainText;
            closeButton.BorderColor = border;
            closeButton.Click += delegate { dialog.Close(); };
            dialog.Controls.Add(closeButton);

            RoundedButton retryButton = new RoundedButton(10);
            retryButton.Text = "Tentar novamente";
            retryButton.SetBounds(352, 276, 128, 38);
            retryButton.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            retryButton.BackColor = Theme.Accent;
            retryButton.ForeColor = Theme.OnAccent;
            retryButton.Look = ButtonLook.Primary;
            retryButton.Click += delegate { result = true; dialog.Close(); };
            dialog.Controls.Add(retryButton);

            dialog.Shown += delegate { retryButton.Focus(); };
            dialog.ShowDialog(this);
        }
        return result;
    }

    // ----- Ajustes finais de áudio: fade in/out e volume igualado (FFmpeg) -----

    private bool NeedsPostProcessing(DownloadJob job)
    {
        if (job.Spec.IsLocal) return false;
        return queueNormalize || job.FadeIn > 0.05 || job.FadeOut > 0.05;
    }

    private double ProbeDuration(string file)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(AppFolder, "ffprobe.exe");
            info.Arguments = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"" + file + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);
                double value;
                return Double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
            }
        }
        catch { return 0; }
    }

    private static string Num(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private bool RunFfmpeg(string arguments)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(AppFolder, "ffmpeg.exe");
            info.Arguments = "-nostdin -hide_banner -loglevel error -y " + arguments;
            info.WorkingDirectory = AppFolder;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(30 * 60 * 1000))
                {
                    try { process.Kill(); }
                    catch { }
                    return false;
                }
                return process.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    // Reprocessa só o áudio. Se algo falhar, o arquivo original é mantido (nunca perde o download).
    private void PostProcessJob(DownloadJob job)
    {
        if (String.IsNullOrEmpty(job.TempDir) || !Directory.Exists(job.TempDir)) return;
        string format = (job.Spec.Format ?? "").ToLowerInvariant();
        string extension = "." + format;
        foreach (string file in Directory.GetFiles(job.TempDir))
        {
            if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            double length = ProbeDuration(file);
            List<string> filters = new List<string>();
            if (queueNormalize) filters.Add("loudnorm=I=-14:TP=-1.5:LRA=11");
            if (job.FadeIn > 0.05) filters.Add("afade=t=in:st=0:d=" + Num(job.FadeIn));
            if (job.FadeOut > 0.05 && length > job.FadeOut) filters.Add("afade=t=out:st=" + Num(length - job.FadeOut) + ":d=" + Num(job.FadeOut));
            if (filters.Count == 0) continue;
            string filter = " -af \"" + String.Join(",", filters.ToArray()) + "\" ";
            string output = Path.Combine(job.TempDir, Path.GetFileNameWithoutExtension(file) + ".ajustado" + extension);
            string input = "-i \"" + file + "\" ";

            string withCover;
            string audioOnly;
            if (job.Spec.IsVideo)
            {
                string audioCodec = format == "webm" ? "-c:a libopus -b:a 160k" : "-c:a aac -b:a 192k";
                string extra = format == "mp4" ? " -movflags +faststart" : "";
                withCover = input + "-map 0:v? -map 0:a -map 0:s? -map_metadata 0 -c:v copy -c:s copy" + filter + audioCodec + " -ar 48000" + extra;
                audioOnly = null;
            }
            else
            {
                string audioCodec = AudioCodecArgs(format, job.Spec.Quality);
                bool coverSupported = format == "mp3" || format == "m4a" || format == "flac";
                audioOnly = input + "-map 0:a -map_metadata 0" + filter + audioCodec;
                withCover = coverSupported
                    ? input + "-map 0:a -map 0:v? -map_metadata 0 -c:v copy -disposition:v attached_pic" + filter + audioCodec
                    : audioOnly;
            }

            bool ok = RunFfmpeg(withCover + " \"" + output + "\"");
            if (!ok && audioOnly != null && audioOnly != withCover) ok = RunFfmpeg(audioOnly + " \"" + output + "\"");
            if (ok && File.Exists(output) && new FileInfo(output).Length > 0)
            {
                try
                {
                    File.Delete(file);
                    File.Move(output, file);
                }
                catch { }
            }
            else
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch { }
            }
        }
    }

    private static string ShortTitle(string url)
    {
        if (IsLocalMedia(url)) return Path.GetFileName(url.Trim().Trim('"'));
        Uri uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "Link";
        string id = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ? uri.AbsolutePath.Trim('/') : uri.Query;
        return String.IsNullOrEmpty(id) ? uri.Host : id.Length > 32 ? id.Substring(0, 32) + "…" : id;
    }

}

internal static class Program
{
    internal static string RuntimeDirectory { get; private set; }

    // Pasta do yt-dlp em uso: a embutida no app ou a última versão baixada pela atualização automática.
    internal static string EnginePointerPath { get { return Path.Combine(RuntimeDirectory, "yt-dlp-atual.txt"); } }

    internal static string YtDlpPath
    {
        get
        {
            try
            {
                if (File.Exists(EnginePointerPath))
                {
                    string folder = File.ReadAllText(EnginePointerPath).Trim();
                    string candidate = Path.Combine(Path.Combine(RuntimeDirectory, folder), "yt-dlp.exe");
                    if (folder.Length > 0 && File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return Path.Combine(Path.Combine(RuntimeDirectory, "yt-dlp"), "yt-dlp.exe");
        }
    }

    internal static void CleanupOldEngines(string keep)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(RuntimeDirectory, "yt-dlp-*"))
            {
                string name = Path.GetFileName(dir);
                if (String.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }
        catch { }
    }

    [STAThread]
    private static void Main()
    {
        // O GitHub exige TLS 1.2 (usado nas atualizações do app e do motor).
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
        catch { }
        try { RuntimeDirectory = PrepareRuntimeDirectory(); }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível preparar os componentes internos do aplicativo.\r\n\r\n" + ex.Message,
                "YT Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new DownloaderForm());
    }

    private static string PrepareRuntimeDirectory()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localData, Path.Combine("YT Downloader", "runtime-v5"));
        Directory.CreateDirectory(directory);
        ExtractGzipResource("YTDownloader.Resources.ffmpeg.exe.gz", Path.Combine(directory, "ffmpeg.exe"), 35L * 1024 * 1024);
        ExtractGzipResource("YTDownloader.Resources.ffprobe.exe.gz", Path.Combine(directory, "ffprobe.exe"), 35L * 1024 * 1024);
        ExtractGzipResource("YTDownloader.Resources.LICENSE-FFmpeg.txt", Path.Combine(directory, "LICENSE-FFmpeg.txt"), 30000);
        ExtractGzipResource("YTDownloader.Resources.LICENSE-yt-dlp.txt", Path.Combine(directory, "LICENSE-yt-dlp.txt"), 1000);
        ExtractGzipResource("YTDownloader.Resources.FFmpeg-BUILD-INFO.txt", Path.Combine(directory, "FFmpeg-BUILD-INFO.txt"), 1000);
        ExtractGzipResource("YTDownloader.Resources.THIRD-PARTY-NOTICES.txt", Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"), 500);
        ExtractZipResource("YTDownloader.Resources.yt-dlp_win.zip", Path.Combine(directory, "yt-dlp"), "yt-dlp.exe");
        return directory;
    }

    // Extrai o yt-dlp "em pasta": diferente do .exe único, ele não se descompacta a cada uso (1-3 s a menos por operação).
    private static void ExtractZipResource(string resourceName, string targetFolder, string requiredFile)
    {
        if (File.Exists(Path.Combine(targetFolder, requiredFile))) return;
        string zipPath = targetFolder + ".zip";
        string staging = targetFolder + ".extraindo";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (resource == null) throw new InvalidOperationException("Recurso interno ausente: " + resourceName);
            using (FileStream output = File.Create(zipPath)) resource.CopyTo(output);
        }
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);
        if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, true);
        Directory.Move(staging, targetFolder);
    }

    private static void ExtractGzipResource(string resourceName, string destination, long minimumSize)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length >= minimumSize) return;
        string temporary = destination + ".extracting";
        if (File.Exists(temporary)) File.Delete(temporary);
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (resource == null) throw new InvalidOperationException("Recurso interno ausente: " + resourceName);
            using (GZipStream gzip = new GZipStream(resource, CompressionMode.Decompress))
            using (FileStream output = File.Create(temporary)) gzip.CopyTo(output);
        }
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temporary, destination);
    }

}
