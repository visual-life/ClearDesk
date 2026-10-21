using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ClearDesk
{
    internal static class OrganizerFiles
    {
        internal static string Folder(BoxData box)
        {
            if (box.Desktop && !Program.Preview) return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Guid id; string segment = Guid.TryParse(box.Id, out id) ? id.ToString("N") : "desktop";
            return Path.Combine(Program.Data, "Files", segment);
        }
        internal static bool ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") return false;
            string stem = name.Split('.')[0].ToUpperInvariant();
            return !new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem);
        }
        internal static string Create(BoxData box, string name, bool directory)
        {
            if (!ValidName(name)) throw new ArgumentException("名称不可为空，不能包含路径或 Windows 保留字符。");
            string folder = Folder(box); Directory.CreateDirectory(folder);
            string stem = directory ? name : Path.GetFileNameWithoutExtension(name), extension = directory ? "" : Path.GetExtension(name);
            for (int i=0;i<10000;i++)
            {
                string path = Path.Combine(folder, i==0 ? name : stem+" ("+i+")"+extension);
                if(File.Exists(path) || Directory.Exists(path)) continue;
                if (directory) Directory.CreateDirectory(path);
                else
                {
                    try { using (var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { } }
                    catch(IOException) { if(File.Exists(path)||Directory.Exists(path)) continue; throw; }
                }
                if(!box.Items.Contains(path,StringComparer.OrdinalIgnoreCase)) box.Items.Add(path);
                box.Excluded.RemoveAll(p=>string.Equals(p,path,StringComparison.OrdinalIgnoreCase));
                Program.Save(); return path;
            }
            throw new IOException("同名项目过多，请更换名称。");
        }
        internal static void Prompt(Window owner, BoxData box, string initial, bool directory, Action refresh)
        {
            var dialog = new Window { Owner=owner, Title=directory?"新建文件夹":"新建文件", Width=440, SizeToContent=SizeToContent.Height, ResizeMode=ResizeMode.NoResize, WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=System.Windows.Media.Brushes.White, Resources=SettingsTheme.Create() };
            var panel=new StackPanel { Margin=new Thickness(24) };
            panel.Children.Add(new TextBlock { Text="名称", Margin=new Thickness(0,0,0,8) });
            var name=new TextBox { Text=initial }; panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text="保存到："+Folder(box)+"\n同名项目自动编号，不覆盖原文件。", TextWrapping=TextWrapping.Wrap, Foreground=System.Windows.Media.Brushes.SlateGray, Margin=new Thickness(0,12,0,12) });
            var error=new TextBlock { Foreground=System.Windows.Media.Brushes.IndianRed, TextWrapping=TextWrapping.Wrap }; panel.Children.Add(error);
            var actions=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right };
            var cancel=new Button { Content="取消",IsCancel=true,Margin=new Thickness(0,8,8,0) }; actions.Children.Add(cancel);
            var create=new Button { Content="创建",IsDefault=true,Margin=new Thickness(0,8,0,0) }; actions.Children.Add(create);
            create.Click+=delegate { try { Create(box,name.Text,directory); refresh(); dialog.Close(); } catch(Exception ex) { error.Text=ex.Message; } };
            panel.Children.Add(actions); dialog.Content=panel; dialog.Loaded+=delegate { name.Focus(); name.SelectAll(); }; dialog.ShowDialog();
        }
    }
}
