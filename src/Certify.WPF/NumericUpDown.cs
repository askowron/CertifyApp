using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Certify.WPF;

/// <summary>
/// Pole liczbowe ze strzalkami gora/dol (WPF nie ma wbudowanego NumericUpDown).
/// Zmiana wartosci: przyciski, strzalki/PageUp/PageDown na klawiaturze, kolko myszy.
/// Wpisywanie tylko cyfr; wartosc przycinana do [Minimum, Maximum] po utracie fokusu.
/// </summary>
public class NumericUpDown : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericUpDown),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumericUpDown), new PropertyMetadata(0, OnRangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumericUpDown), new PropertyMetadata(100, OnRangeChanged));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumericUpDown), new PropertyMetadata(1));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public event EventHandler? ValueChanged;

    private readonly TextBox _box;
    private readonly Border _frame;
    private bool _typing;

    public NumericUpDown()
    {
        _box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 7, 4, 7),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Text = "0"
        };
        // Bez ramki z globalnego stylu TextBox - ramke rysuje _frame wokol calosci.
        _box.Template = BuildPlainTextBoxTemplate();
        _box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(_box, OnPaste);
        _box.LostKeyboardFocus += (_, _) => CommitText();
        // Value aktualne juz w trakcie pisania (Enter na domyslnym "Zapisz" nie zabiera fokusu);
        // tekstu nie przepisujemy w locie - normalizacja dopiero w CommitText.
        _box.TextChanged += (_, _) =>
        {
            if (!int.TryParse(_box.Text, out var v)) return;
            _typing = true;
            try { SetCurrentValue(ValueProperty, v); }
            finally { _typing = false; }
        };
        _box.PreviewKeyDown += OnKeyDown;
        _box.GotKeyboardFocus += (_, _) => UpdateFrame();
        _box.LostKeyboardFocus += (_, _) => UpdateFrame();

        var up = MakeArrowButton("M 0 4 L 4 0 L 8 4 Z", +1);
        var down = MakeArrowButton("M 0 0 L 4 4 L 8 0 Z", -1);
        var arrows = new Grid { Width = 22 };
        arrows.RowDefinitions.Add(new RowDefinition());
        arrows.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(down, 1);
        arrows.Children.Add(up);
        arrows.Children.Add(down);
        var sep = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush2"),
            Child = arrows
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sep, 1);
        grid.Children.Add(_box);
        grid.Children.Add(sep);

        _frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x31)),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush2"),
            SnapsToDevicePixels = true,
            ClipToBounds = true,
            Child = grid
        };
        Content = _frame;
        MouseEnter += (_, _) => UpdateFrame();
        MouseLeave += (_, _) => UpdateFrame();
        // Focus na kontrolce -> do pola tekstowego (Tab, etykiety, ToolTip).
        Focusable = false;
    }

    private static ControlTemplate BuildPlainTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(MarginProperty, new TemplateBindingExtension(PaddingProperty));
        sv.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        t.VisualTree = sv;
        return t;
    }

    private RepeatButton MakeArrowButton(string geometry, int direction)
    {
        var path = new Path
        {
            Data = Geometry.Parse(geometry),
            Width = 8, Height = 4,
            Fill = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new RepeatButton
        {
            Content = path,
            Focusable = false,
            Cursor = Cursors.Hand,
            Delay = 400,
            Interval = 60
        };
        var t = new ControlTemplate(typeof(RepeatButton));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x23, 0x36, 0x5C)), "Bd"));
        t.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, (Brush)Application.Current.FindResource("AccentBrush"), "Bd"));
        t.Triggers.Add(pressed);
        btn.Template = t;
        btn.Click += (_, _) => { CommitText(); Value += direction * Step; _box.Focus(); };
        return btn;
    }

    private void UpdateFrame()
    {
        _frame.BorderBrush = _box.IsKeyboardFocused
            ? (Brush)Application.Current.FindResource("AccentBrush")
            : IsMouseOver
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x4A, 0x7A))
                : (Brush)Application.Current.FindResource("BorderBrush2");
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Up => Step,
            Key.Down => -Step,
            Key.PageUp => Step * 10,
            Key.PageDown => -Step * 10,
            _ => 0
        };
        if (delta != 0) { CommitText(); Value += delta; _box.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Enter) CommitText();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Tylko gdy pole ma fokus - inaczej przewijanie okna "kradloby" wartosc.
        if (!_box.IsKeyboardFocused) return;
        CommitText();
        Value += Math.Sign(e.Delta) * Step;
        e.Handled = true;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string s || !s.All(char.IsDigit)) e.CancelCommand();
    }

    private void CommitText()
    {
        if (int.TryParse(_box.Text, out var v)) Value = v;
        _box.Text = Value.ToString();
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var c = (NumericUpDown)d;
        return Math.Clamp((int)baseValue, c.Minimum, Math.Max(c.Minimum, c.Maximum));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (NumericUpDown)d;
        if (!c._typing) c._box.Text = ((int)e.NewValue).ToString();
        c.ValueChanged?.Invoke(c, EventArgs.Empty);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.CoerceValue(ValueProperty);
}
