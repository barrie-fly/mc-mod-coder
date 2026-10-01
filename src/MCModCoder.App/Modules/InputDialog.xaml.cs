using System.Windows;

namespace MCModCoder.Modules;

/// <summary>通用输入对话框</summary>
public partial class InputDialog : Window
{
    public string Value { get; private set; } = "";

    public InputDialog(string title, string defaultValue = "")
    {
        InitializeComponent();
        WindowIcons.Apply(this); // 图标安全加载（见 WindowIcons 注释）
        Title = title;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = InputBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Ok_Click(sender, e);
    }
}
