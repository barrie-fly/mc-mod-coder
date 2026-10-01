using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MCModCoder.Modules;

public class RecentItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        AppContext.ProjectChanged += Refresh;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var p = AppContext.CurrentProject;
        ProjectCard.Text = p == null
            ? "尚未打开项目。点击右上角「新建项目」，选择 Minecraft 版本与加载器即可开始。"
            : $"{p.Name}  ·  MC {p.McVersion}  ·  {p.ModLoader}  ·  {p.Package}\n{p.ProjectDir}";
        var items = AppContext.RecentProjects.Select(r => new RecentItem { Name = r.Name, Path = r.Path }).ToList();
        RecentList.ItemsSource = items;
    }

    private void OpenRecent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem it)
        {
            if (System.IO.File.Exists(it.Path))
            {
                try
                {
                    var p = System.Text.Json.JsonSerializer.Deserialize<Core.Models.McProject>(
                        System.IO.File.ReadAllText(it.Path),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (p != null) AppContext.OpenProject(p);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show("打开失败：" + ex.Message);
                }
            }
            else
            {
                MessageBox.Show("项目文件不存在：" + it.Path);
            }
        }
    }
}
