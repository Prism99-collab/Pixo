using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Preferences' design kit: one graphite ramp, one accent (the theme's), one radius scale, and the controls built from
/// them, so every section looks and behaves the same.
///  • surfaces: window, sidebar, card, control (raised), each a step lighter; hairlines at 7 % white;
///  • text: primary, secondary, muted; the accent only marks what's on or picked, and the one main button;
///  • every control can be reached and used with the keyboard (Tab, Space, Enter), with a ring that shows only then;
///  • a choice is drawn for what it holds: a segmented strip for a few short options, a list with radio marks for long
///    ones, a menu for many.
/// </summary>
public sealed partial class PreferencesWindow
{
    // ───────────── tokens ─────────────

    static readonly Brush Text = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF3));
    static readonly Brush Dim = Frozen(Color.FromRgb(0xA3, 0xA3, 0xAA));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x72, 0x72, 0x7A));
    static readonly Brush Faint = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    static readonly Brush Edge = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    static readonly Brush EdgeHover = Frozen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
    static readonly Brush Raised = Frozen(Color.FromRgb(0x25, 0x25, 0x29));
    static readonly Brush Good = Frozen(Color.FromRgb(0x4C, 0xC3, 0x8A));
    static readonly Brush Warn = Frozen(Color.FromRgb(0xF0, 0xB2, 0x4A));
    static readonly Brush Danger = Frozen(Color.FromRgb(0xF0, 0x6A, 0x63));
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    Brush CardBrush => Frozen(_mica ? Color.FromArgb(0xB8, 0x1A, 0x1A, 0x1D) : Color.FromRgb(0x19, 0x19, 0x1C));
    Brush SideBrush => Frozen(_mica ? Color.FromArgb(0x52, 0x0A, 0x0A, 0x0C) : Color.FromRgb(0x13, 0x13, 0x15));
    Brush WindowBrush => Frozen(Color.FromRgb(0x0F, 0x0F, 0x11));
    /// <summary>The accent, faintly: the background of what's picked.</summary>
    Brush AccentWash => Frozen(Color.FromArgb(0x26, AccentColor.R, AccentColor.G, AccentColor.B));
    /// <summary>Text on the accent: black on a light accent, white on a dark one.</summary>
    Brush AccentInk => Frozen(Luma(AccentColor) > 0.5 ? Color.FromRgb(0x0B, 0x0B, 0x0C) : Colors.White);
    static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
    string AccentHex => $"#{AccentColor.R:X2}{AccentColor.G:X2}{AccentColor.B:X2}";

    // ───────────── styles (made again with each build: the accent follows the theme) ─────────────

    Style _btn = null!, _btnPrimary = null!, _ghost = null!, _bare = null!, _switch = null!;
    readonly Dictionary<double, Style> _rings = new();

    void Kit()
    {
        _rings.Clear();
        string a = AccentHex, ink = Luma(AccentColor) > 0.5 ? "#0B0B0C" : "#FFFFFF";
        const string ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        Style Parse(string xaml) => (Style)System.Windows.Markup.XamlReader.Parse(xaml);

        _btn = Parse($@"
<Style TargetType='Button' {ns}>
  <Setter Property='Background' Value='#25252A' />
  <Setter Property='BorderBrush' Value='#14FFFFFF' />
  <Setter Property='Foreground' Value='#F2F2F3' />
  <Setter Property='Height' Value='32' />
  <Setter Property='Padding' Value='14,0' />
  <Setter Property='FontSize' Value='13' />
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='HorizontalContentAlignment' Value='Center' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Grid x:Name='Root' SnapsToDevicePixels='True'>
        <Border CornerRadius='8' Background='{{TemplateBinding Background}}' BorderBrush='{{TemplateBinding BorderBrush}}' BorderThickness='1' />
        <Border x:Name='Hover' CornerRadius='8' Background='#10FFFFFF' Opacity='0' />
        <ContentPresenter Margin='{{TemplateBinding Padding}}' HorizontalAlignment='{{TemplateBinding HorizontalContentAlignment}}' VerticalAlignment='Center' />
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='1' /></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='0.45' /></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Root' Property='Opacity' Value='0.4' /></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        _btnPrimary = Parse($@"
<Style TargetType='Button' {ns}>
  <Setter Property='Background' Value='{a}' />
  <Setter Property='Foreground' Value='{ink}' />
  <Setter Property='Height' Value='32' />
  <Setter Property='Padding' Value='14,0' />
  <Setter Property='FontSize' Value='13' />
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Border x:Name='Bd' CornerRadius='8' Background='{{TemplateBinding Background}}' Padding='{{TemplateBinding Padding}}' SnapsToDevicePixels='True'>
        <ContentPresenter HorizontalAlignment='{{TemplateBinding HorizontalContentAlignment}}' VerticalAlignment='Center' />
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.88' /></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.74' /></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.4' /></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        // a quiet button: nothing until the pointer comes (icons, the sidebar, menus)
        _ghost = Parse($@"
<Style TargetType='Button' {ns}>
  <Setter Property='Background' Value='Transparent' />
  <Setter Property='Foreground' Value='#F2F2F3' />
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='HorizontalContentAlignment' Value='Center' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Border x:Name='Bd' CornerRadius='8' Background='{{TemplateBinding Background}}' Padding='{{TemplateBinding Padding}}' SnapsToDevicePixels='True'>
        <ContentPresenter HorizontalAlignment='{{TemplateBinding HorizontalContentAlignment}}' VerticalAlignment='Center' />
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#12FFFFFF' /></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='#0AFFFFFF' /></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.3' /></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        // a picture that's a button (theme, style and size cards): it draws itself
        _bare = Parse($@"
<Style TargetType='Button' {ns}>
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'><ContentPresenter /></ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        _switch = Parse($@"
<Style TargetType='ToggleButton' {ns}>
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='ToggleButton'>
      <Grid Width='40' Height='22' Background='Transparent'>
        <Border x:Name='Off' CornerRadius='11' Background='#3A3A40' />
        <Border x:Name='OnTrack' CornerRadius='11' Background='{a}' Opacity='0' />
        <Ellipse x:Name='Knob' Width='16' Height='16' HorizontalAlignment='Left' Margin='3,0,0,0'>
          <Ellipse.Fill><SolidColorBrush Color='#E8E8EA' /></Ellipse.Fill>
          <Ellipse.RenderTransform><TranslateTransform /></Ellipse.RenderTransform>
        </Ellipse>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Off' Property='Background' Value='#47474E' /></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4' /></Trigger>
        <Trigger Property='IsChecked' Value='True'>
          <Trigger.EnterActions><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='18' Duration='0:0:0.22'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut' /></DoubleAnimation.EasingFunction></DoubleAnimation>
            <DoubleAnimation Storyboard.TargetName='OnTrack' Storyboard.TargetProperty='Opacity' To='1' Duration='0:0:0.16' />
            <ColorAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(Shape.Fill).(SolidColorBrush.Color)' To='{ink}' Duration='0:0:0.16' />
          </Storyboard></BeginStoryboard></Trigger.EnterActions>
          <Trigger.ExitActions><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='0' Duration='0:0:0.22'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut' /></DoubleAnimation.EasingFunction></DoubleAnimation>
            <DoubleAnimation Storyboard.TargetName='OnTrack' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:0.16' />
            <ColorAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(Shape.Fill).(SolidColorBrush.Color)' To='#E8E8EA' Duration='0:0:0.16' />
          </Storyboard></BeginStoryboard></Trigger.ExitActions>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        _switch.Setters.Add(new Setter(FocusVisualStyleProperty, Ring(13)));

        // text boxes, password boxes and tooltips: dark, rounded, the accent while you type (none of Windows' light chrome)
        string field(string type) => $@"
<Style TargetType='{type}' {ns}>
  <Setter Property='Foreground' Value='#F2F2F3' />
  <Setter Property='Background' Value='#141416' />
  <Setter Property='BorderBrush' Value='#14FFFFFF' />
  <Setter Property='BorderThickness' Value='1' />
  <Setter Property='CaretBrush' Value='#F2F2F3' />
  <Setter Property='SelectionBrush' Value='{a}' />
  <Setter Property='Padding' Value='10,7' />
  <Setter Property='FontSize' Value='13' />
  <Setter Property='FocusVisualStyle' Value='{{x:Null}}' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='{type}'>
      <Border x:Name='Bd' CornerRadius='8' Background='{{TemplateBinding Background}}' BorderBrush='{{TemplateBinding BorderBrush}}' BorderThickness='{{TemplateBinding BorderThickness}}' SnapsToDevicePixels='True'>
        <ScrollViewer x:Name='PART_ContentHost' Focusable='False' Margin='{{TemplateBinding Padding}}' VerticalAlignment='{{TemplateBinding VerticalContentAlignment}}' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='{{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}}' />
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#2AFFFFFF' /></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='{a}' /></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.5' /></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";
        Resources[typeof(TextBox)] = Parse(field("TextBox"));
        Resources[typeof(PasswordBox)] = Parse(field("PasswordBox"));
        Resources[typeof(ToolTip)] = Parse($@"
<Style TargetType='ToolTip' {ns}>
  <Setter Property='Foreground' Value='#F2F2F3' />
  <Setter Property='FontSize' Value='12' />
  <Setter Property='HasDropShadow' Value='False' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='ToolTip'>
      <Border Background='#26262B' BorderBrush='#1FFFFFFF' BorderThickness='1' CornerRadius='7' Padding='9,5' Margin='0,2' MaxWidth='320'>
        <ContentPresenter><ContentPresenter.Resources><Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap' /></Style></ContentPresenter.Resources></ContentPresenter>
      </Border>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
        Resources[typeof(ScrollBar)] = ScrollStyle();
    }

    /// <summary>The keyboard's ring (shown only when you move with the keyboard), for a shape of this radius.</summary>
    Style Ring(double radius)
    {
        if (_rings.TryGetValue(radius, out var s)) return s;
        var t = new ControlTemplate();
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius + 3));
        bd.SetValue(Border.BorderBrushProperty, Accent);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        bd.SetValue(MarginProperty, new Thickness(-3));
        bd.SetValue(SnapsToDevicePixelsProperty, true);
        t.VisualTree = bd;
        s = new Style { Setters = { new Setter(Control.TemplateProperty, t) } };
        return _rings[radius] = s;
    }

    // ───────────── pieces ─────────────

    static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
    };

    /// <summary>A button: a quiet one, or the accent one (one per place at most).</summary>
    Button PillButton(string? glyph, string text, Action act, bool primary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(glyph)) row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, Margin = new Thickness(0, 1, text.Length > 0 ? 8 : 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (text.Length > 0) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Style = primary ? _btnPrimary : _btn, FocusVisualStyle = Ring(8) };
        if (text.Length == 0) { b.Width = 32; b.Padding = new Thickness(0); }
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>A chip in a row of picks: picked, it takes the accent's wash and edge.</summary>
    void SetPill(Button b, bool on)
    {
        b.Background = on ? AccentWash : Raised;
        b.BorderBrush = on ? Accent : Edge;
        b.Foreground = Text;
    }

    /// <summary>A small icon-only button (move up and down, delete, play).</summary>
    Button IconButton(string glyph, string tip, Action act, bool enabled = true)
    {
        var b = new Button
        {
            Content = Glyph(glyph, 12, enabled ? Dim : Muted), Width = 30, Height = 30, Style = _ghost, FocusVisualStyle = Ring(8),
            ToolTip = Loc.T(tip), IsEnabled = enabled,
        };
        AutomationName(b, Loc.T(tip));
        b.MouseEnter += (_, _) => { if (b.Content is TextBlock g) g.Foreground = Text; };
        b.MouseLeave += (_, _) => { if (b.Content is TextBlock g) g.Foreground = b.IsEnabled ? Dim : Muted; };
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>A picture to pick (a theme, a style, a size): a card that's a real button, with the picked one ringed in the accent.</summary>
    Button PickCard(UIElement picture, string name, bool on, Action pick, string? sub = null)
    {
        var body = new StackPanel();
        body.Children.Add(picture);
        var caption = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = on ? Text : Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub != null) words.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 1, 0, 0) });
        caption.Children.Add(words);
        if (on) caption.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Accent, HorizontalAlignment = HorizontalAlignment.Right,
            Child = Glyph("", 9, AccentInk),
        });
        body.Children.Add(caption);
        var card = new Border
        {
            Child = body, CornerRadius = new CornerRadius(12), Background = CardBrush, ClipToBounds = true,
            BorderBrush = on ? Accent : Edge, BorderThickness = new Thickness(1.5), // (the same thickness picked or not: nothing shifts)
        };
        var b = new Button { Content = card, Style = _bare, Margin = new Thickness(0, 0, 8, 8), FocusVisualStyle = Ring(12) };
        AutomationName(b, name);
        b.MouseEnter += (_, _) => { if (!on) card.BorderBrush = EdgeHover; };
        b.MouseLeave += (_, _) => { if (!on) card.BorderBrush = Edge; };
        b.Click += (_, _) => { if (!on) pick(); };
        return b;
    }

    /// <summary>The top of a picture card: a stage of this height, its corners rounded like the card's.</summary>
    static Border Stage(Grid stage)
    {
        // clipped to the card's rounded top (a plain clip is square: the picture's corners would poke past the card's)
        stage.SizeChanged += (_, e) => stage.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height + 12), 10.5, 10.5);
        return new Border { Child = stage };
    }

    static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    void Header(string title, string sub)
    {
        _content.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = DisplayFont });
        if (sub.Length > 0) _content.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        _content.Children.Add(new Border { Height = 20 });
    }

    /// <summary>A group's title (and where the search jumps to).</summary>
    void Label(string text)
    {
        bool first = _content.Children.Count > 0 && _content.Children[^1] is Border { Height: 20, Child: null }; // (right under the section's title)
        _content.Children.Add(new TextBlock
        {
            Text = text, Tag = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Text, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, first ? 0 : 24, 0, 10),
        });
    }

    /// <summary>A short line under a group, in the secondary colour.</summary>
    void Note(string text, double top = 0) => _content.Children.Add(new TextBlock
    {
        Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, top, 0, 8), LineHeight = 17,
    });

    void Card(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var stack = new StackPanel();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) stack.Children.Add(Divider(56));
            stack.Children.Add(RowView(list[i]));
        }
        _content.Children.Add(CardOf(stack));
    }

    /// <summary>A hairline between rows, starting where the text does.</summary>
    static Border Divider(double inset = 16) => new() { Height = 1, Background = Edge, Margin = new Thickness(inset, 0, 0, 0) };

    Border CardOf(UIElement child, Thickness? padding = null) => new()
    {
        CornerRadius = new CornerRadius(12), Background = CardBrush, BorderBrush = Edge, BorderThickness = new Thickness(1),
        Padding = padding ?? new Thickness(0), Margin = new Thickness(0, 0, 0, 8), Child = child,
    };

    /// <summary>A setting with a switch: its icon, its name and what it does; the whole row is the switch's label.</summary>
    FrameworkElement RowView(Row r)
    {
        var g = new Grid { MinHeight = 60, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Faint, Child = Glyph(r.Glyph, 14, Dim), HorizontalAlignment = HorizontalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 16, 12) };
        text.Children.Add(new TextBlock { Text = Loc.T(r.Title), FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        if (r.Desc.Length > 0) text.Children.Add(new TextBlock { Text = Loc.T(r.Desc), FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 17 });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var sw = new ToggleButton { Style = _switch, IsChecked = r.Get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        AutomationName(sw, Loc.T(r.Title));
        sw.Click += (_, _) =>
        {
            r.Set(sw.IsChecked == true);
            _notch.PrefChanged();
            sw.IsChecked = r.Get(); // some switches ask Windows first (FPS): show what really happened
            Saved();
        };
        Grid.SetColumn(sw, 2);
        g.Children.Add(sw);
        g.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindUp<ToggleButton>(d)) return; // (the switch handles its own click)
            sw.IsChecked = !(sw.IsChecked == true);
            sw.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        };
        return g;
    }

    // ───────────── choices ─────────────

    /// <summary>
    /// One of a few: a segmented strip when the options are few and short, a list with radio marks when they're
    /// sentences, a menu when there are many. Picking saves at once.
    /// </summary>
    void Choice(string title, (string Key, string Name)[] options, Func<string> get, Action<string> set)
    {
        Label(title);
        string now = get();
        if (!options.Any(o => o.Key == now) && options.Length > 0) now = options[0].Key;
        void Pick(string k)
        {
            set(k);
            _notch.PrefChanged();
            Saved();
        }
        if (options.Length > 6) _content.Children.Add(Dropdown(options, now, Pick));
        else if (options.Any(o => o.Name.Length > 26)) _content.Children.Add(RadioList(options, now, Pick));
        else _content.Children.Add(Segmented(options, now, Pick));
    }

    FrameworkElement Segmented((string Key, string Name)[] options, string now, Action<string> pick)
    {
        var strip = new WrapPanel();
        var host = new Border
        {
            Background = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Child = strip,
        };
        var segs = new List<(Button B, TextBlock T, string Key)>();
        void Paint(string picked)
        {
            foreach (var (b, t, k) in segs)
            {
                bool on = k == picked;
                b.Background = on ? Raised : Brushes.Transparent;
                t.Foreground = on ? Text : Dim;
                b.BorderBrush = on ? EdgeHover : Brushes.Transparent;
            }
        }
        foreach (var (key, name) in options)
        {
            var t = new TextBlock { Text = name, FontSize = 12.5 };
            var b = new Button { Content = t, Style = _ghost, Height = 28, Padding = new Thickness(12, 0, 12, 0), FocusVisualStyle = Ring(7), BorderThickness = new Thickness(1) };
            b.Template = SegTemplate();
            string k = key;
            b.Click += (_, _) => { Paint(k); pick(k); };
            segs.Add((b, t, k));
            strip.Children.Add(b);
        }
        Paint(now);
        return host;
    }

    static ControlTemplate? _segTemplate;
    static ControlTemplate SegTemplate()
    {
        if (_segTemplate != null) return _segTemplate;
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        bd.SetValue(SnapsToDevicePixelsProperty, true);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85, "Bd"));
        t.Triggers.Add(hover);
        t.Seal();
        return _segTemplate = t;
    }

    FrameworkElement RadioList((string Key, string Name)[] options, string now, Action<string> pick)
    {
        var stack = new StackPanel();
        var marks = new List<(Ellipse Ring, Ellipse Dot, TextBlock T, string Key)>();
        void Paint(string picked)
        {
            foreach (var (ring, dot, t, k) in marks)
            {
                bool on = k == picked;
                ring.Stroke = on ? Accent : Muted;
                dot.Opacity = on ? 1 : 0;
                t.Foreground = on ? Text : Dim;
            }
        }
        for (int i = 0; i < options.Length; i++)
        {
            var (key, name) = options[i];
            if (i > 0) stack.Children.Add(Divider(48));
            var g = new Grid { MinHeight = 46 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ring = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1.5 };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Accent };
            var mark = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { ring, dot } };
            g.Children.Add(mark);
            var t = new TextBlock { Text = name, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var b = new Button { Content = g, Style = _ghost, HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = Ring(8) };
            AutomationName(b, name);
            string k = key;
            b.Click += (_, _) => { Paint(k); pick(k); };
            marks.Add((ring, dot, t, k));
            stack.Children.Add(b);
        }
        Paint(now);
        return CardOf(stack, new Thickness(0, 2, 0, 2));
    }

    FrameworkElement Dropdown((string Key, string Name)[] options, string now, Action<string> pick)
    {
        string NameOf(string k) => options.FirstOrDefault(o => o.Key == k).Name ?? "";
        var shown = new TextBlock { Text = NameOf(now), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var face = new Grid();
        face.ColumnDefinitions.Add(new ColumnDefinition());
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        face.Children.Add(shown);
        var chevron = Glyph("", 10, Dim);
        chevron.Margin = new Thickness(12, 1, 0, 0);
        Grid.SetColumn(chevron, 1);
        face.Children.Add(chevron);
        var button = new Button { Content = face, Style = _btn, MinWidth = 260, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = Ring(8) };
        AutomationName(button, NameOf(now));

        var list = new StackPanel();
        var menu = new Border
        {
            Background = Frozen(Color.FromRgb(0x20, 0x20, 0x24)), BorderBrush = EdgeHover, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            Margin = new Thickness(12, 0, 12, 18), // (room for the shadow)
            Child = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
        };
        var popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 4, HorizontalOffset = -12, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, Child = menu, FlowDirection = FlowDirection,
        };
        menu.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; button.Focus(); e.Handled = true; } };
        void Fill()
        {
            list.Children.Clear();
            foreach (var (key, name) in options)
            {
                bool on = key == now;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                if (on) row.Children.Add(Glyph("", 11, Accent));
                var t = new TextBlock { Text = name, FontSize = 13, Foreground = on ? Text : Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                var item = new Button { Content = row, Style = _ghost, Height = 34, Padding = new Thickness(4, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = Ring(8) };
                string k = key;
                item.Click += (_, _) =>
                {
                    popup.IsOpen = false;
                    if (k == now) return;
                    now = k;
                    shown.Text = NameOf(k);
                    AutomationName(button, shown.Text);
                    pick(k);
                };
                list.Children.Add(item);
            }
        }
        button.Click += (_, _) =>
        {
            Fill();
            menu.MinWidth = button.ActualWidth;
            popup.IsOpen = true;
            // the picked one in view, and the keyboard on it
            if (list.Children.OfType<Button>().ElementAtOrDefault(Array.FindIndex(options, o => o.Key == now)) is { } picked)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { picked.BringIntoView(); picked.Focus(); });
        };
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform();
        popup.Opened += (_, _) => ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(180, TimeSpan.FromMilliseconds(160)));
        popup.Closed += (_, _) => ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        var host = new Grid { Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(button);
        host.Children.Add(popup);
        return host;
    }

    // ───────────── fields ─────────────

    /// <summary>A rounded field around a box (a password, a model id, your instructions): the accent edge while you type in it.</summary>
    Border FieldHost(UIElement box, Thickness margin)
    {
        var host = new Border { CornerRadius = new CornerRadius(8), Background = Frozen(Color.FromRgb(0x14, 0x14, 0x16)), BorderBrush = Edge, BorderThickness = new Thickness(1), Child = box, Margin = margin, SnapsToDevicePixels = true };
        host.IsKeyboardFocusWithinChanged += (_, _) => host.BorderBrush = host.IsKeyboardFocusWithin ? Accent : Edge;
        host.MouseEnter += (_, _) => { if (!host.IsKeyboardFocusWithin) host.BorderBrush = EdgeHover; };
        host.MouseLeave += (_, _) => { if (!host.IsKeyboardFocusWithin) host.BorderBrush = Edge; };
        return host;
    }

    // ───────────── "Saved" ─────────────

    Border _toast = null!;
    readonly TranslateTransform _toastShift = new();

    FrameworkElement Toast()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph("", 11, Good));
        row.Children.Add(new TextBlock { Text = Loc.T("Saved"), FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _toast = new Border
        {
            Child = row, Background = Frozen(Color.FromRgb(0x26, 0x26, 0x2B)), BorderBrush = EdgeHover, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20),
            Opacity = 0, IsHitTestVisible = false, RenderTransform = _toastShift,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black },
        };
        return _toast;
    }

    /// <summary>"Saved" rises in at the bottom, stays a moment, and goes.</summary>
    void Saved()
    {
        if (_toast == null) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1700) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1250))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        _toast.BeginAnimation(OpacityProperty, a);
        _toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
