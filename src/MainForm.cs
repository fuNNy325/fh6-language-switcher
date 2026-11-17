using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HorizonLanguage
{
    public sealed class MainForm : Form
    {
        private const string DefaultPath = @"F:\SteamLibrary\steamapps\common\ForzaHorizon6";
        private static readonly Color Ink = Color.FromArgb(25, 40, 62);
        private static readonly Color Muted = Color.FromArgb(92, 109, 130);
        private static readonly Color Accent = Color.FromArgb(0, 106, 106);
        private readonly TextBox path = new TextBox();
        private readonly ComboBox languages = new ComboBox();
        private readonly Label status = new Label();
        private readonly Label detail = new Label();
        private readonly Label preview = new Label();
        private readonly TextBox log = new TextBox();
        private readonly Button apply = new Button();
        private readonly Button restore = new Button();
        private readonly Button recover = new Button();
        private readonly Button baseline = new Button();
        private readonly List<Control> interactive = new List<Control>();
        private SwapEngine engine;
        private Inspection inspection;
        private bool busy;

        public MainForm(string initialPath)
        {
            Text = "FH6 Language Switcher · 语言包换名工具";
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = Ink;
            BackColor = Color.FromArgb(242, 245, 248);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(880, 758);
            MinimumSize = new Size(820, 760);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24, 18, 24, 14) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 123));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            Controls.Add(layout);

            var hero = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Margin = new Padding(0, 0, 0, 12) };
            hero.Controls.Add(LabelAt("FH6  /  LANGUAGE SWITCHER", 20, 10, 600, 24, 10, Color.FromArgb(128, 219, 205), FontStyle.Bold));
            hero.Controls.Add(LabelAt("保留中文，换一种声音", 18, 34, 650, 38, 21, Color.White, FontStyle.Bold));
            layout.Controls.Add(hero, 0, 0);

            var folder = Card();
            folder.Controls.Add(LabelAt("01   游戏位置", 16, 9, 180, 24, 10, Ink, FontStyle.Bold));
            folder.Controls.Add(LabelAt("选择游戏根目录，或直接选择 StringTables 文件夹", 16, 34, 700, 22, 9, Muted, FontStyle.Regular));
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 49, Padding = new Padding(16, 0, 16, 10), ColumnCount = 4 };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            path.Dock = DockStyle.Fill;
            path.Margin = new Padding(0, 5, 8, 0);
            path.Text = initialPath ?? DefaultPath;
            path.AccessibleName = "游戏目录";
            folderRow.Controls.Add(path, 0, 0);
            Button browse = MakeButton("浏览…", false);
            browse.Click += async (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择 ForzaHorizon6 根目录或 StringTables", ShowNewFolderButton = false })
                {
                    if (Directory.Exists(path.Text)) dialog.SelectedPath = path.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) { path.Text = dialog.SelectedPath; await Scan(); }
                }
            };
            folderRow.Controls.Add(browse, 1, 0);
            Button auto = MakeButton("自动查找", false);
            auto.Click += async (s, e) =>
            {
                await Run(async () =>
                {
                    string found = await Task.Run(() => FindGame());
                    if (found == null) throw new IOException("未在常见 Steam 库中找到游戏，请点击“浏览”手动选择。");
                    path.Text = found;
                });
                await Scan();
            };
            folderRow.Controls.Add(auto, 2, 0);
            Button scan = MakeButton("检查", false);
            scan.Click += async (s, e) => await Scan();
            folderRow.Controls.Add(scan, 3, 0);
            folder.Controls.Add(folderRow);
            layout.Controls.Add(folder, 0, 1);

            var languageCard = Card();
            languageCard.Controls.Add(LabelAt("02   目标语言", 16, 9, 190, 24, 10, Ink, FontStyle.Bold));
            languageCard.Controls.Add(LabelAt("在游戏 / Steam 中选择该语言，让其读取换名后的中文字符表。", 16, 35, 750, 23, 9, Muted, FontStyle.Regular));
            languages.DropDownStyle = ComboBoxStyle.DropDownList;
            languages.Location = new Point(18, 68);
            languages.Size = new Size(264, 30);
            languages.AccessibleName = "目标语言";
            languages.SelectedIndexChanged += (s, e) => UpdatePreview();
            languageCard.Controls.Add(languages);
            preview.Location = new Point(304, 65);
            preview.Size = new Size(470, 45);
            preview.ForeColor = Accent;
            preview.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            languageCard.Controls.Add(preview);
            layout.Controls.Add(languageCard, 0, 2);

            var stateCard = Card();
            status.Location = new Point(16, 12);
            status.Size = new Size(760, 26);
            status.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            status.Text = "尚未检查目录";
            detail.Location = new Point(16, 42);
            detail.Size = new Size(760, 43);
            detail.ForeColor = Muted;
            detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            detail.Text = "首次使用前，请确保语言包未经手动交换。";
            stateCard.Controls.Add(status);
            stateCard.Controls.Add(detail);
            layout.Controls.Add(stateCard, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            StyleButton(apply, "备份并交换文件名", true);
            StyleButton(restore, "还原文件名", false);
            StyleButton(recover, "恢复中断操作", false);
            StyleButton(baseline, "重建备份基线", false);
            actions.Controls.Add(apply, 0, 0);
            actions.Controls.Add(restore, 1, 0);
            actions.Controls.Add(recover, 2, 0);
            actions.Controls.Add(baseline, 3, 0);
            layout.Controls.Add(actions, 0, 4);
            apply.Click += async (s, e) =>
            {
                LanguageItem item = languages.SelectedItem as LanguageItem;
                if (engine == null || item == null) return;
                string message = "即将交换 CHS.zip 与 " + item.File + " 的文件名。\r\n\r\n" +
                    "请先完全退出游戏。首次备份会把当前文件视作原始版本；如果之前手动换过名，请先还原。\r\n\r\n" +
                    "操作后，在游戏 / Steam 中选择 " + item + "。实际字幕与配音效果需在游戏中确认。";
                if (MessageBox.Show(this, message, "交换文件名", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                await Mutate(() => engine.Apply(item.File), "已交换文件名：CHS.zip ↔ " + item.File);
            };
            restore.Click += async (s, e) => await Mutate(() => engine.Restore(), "已还原本工具记录的原始文件名。");
            recover.Click += async (s, e) => await Mutate(() => engine.Recover(), "已恢复到中断操作之前的状态。");
            baseline.Click += async (s, e) =>
            {
                if (engine == null) return;
                if (MessageBox.Show(this,
                    "仅在你已通过 Steam 验证游戏文件完整性，或确认所有语言包均为当前游戏版本的原始文件后使用。\r\n\r\n" +
                    "此操作不会还原任何 ZIP。它会归档旧记录，并把当前文件认作新基线；旧备份仍保留。\r\n\r\n确认当前文件名和内容都已还原？",
                    "重建备份基线", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                await Mutate(() => engine.Rebaseline(), "旧备份已保留，下次交换将备份当前版本。");
            };

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.BorderStyle = BorderStyle.None;
            log.BackColor = Color.FromArgb(232, 238, 244);
            log.ForeColor = Muted;
            log.Font = new Font("Microsoft YaHei UI", 9F);
            log.Margin = new Padding(0, 6, 0, 0);
            log.AccessibleName = "操作日志";
            layout.Controls.Add(log, 0, 5);
            var footer = new Label { Text = "本地运行 · ZIP 内容保持原样 · 自动备份与 SHA-256 校验   |   v1.0.0",
                Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 9F) };
            layout.Controls.Add(footer, 0, 6);
            interactive.AddRange(new Control[] { path, browse, auto, scan, languages, apply, restore, recover, baseline });
            path.TextChanged += (s, e) => { engine = null; inspection = null; languages.Items.Clear(); RefreshButtons(); status.Text = "目录已修改，请重新检查"; detail.Text = "检查后会列出此目录下的语言 ZIP。"; };
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; AddLog("正在完成文件操作，请稍候再关闭窗口。"); } };
            Shown += async (s, e) => await Scan();
            AddLog("请在游戏关闭时操作。工具只交换文件名，不修改 ZIP 内部内容。");
            RefreshButtons();
        }

        private async Task Scan()
        {
            string input = path.Text;
            await Run(async () =>
            {
                SwapEngine next = await Task.Run(() => new SwapEngine(input));
                Inspection found = await Task.Run(() => next.Inspect());
                engine = next;
                ShowInspection(found);
                AddLog("检查完成：" + next.TablesDirectory);
            });
        }

        private async Task Mutate(Action action, string success)
        {
            if (engine == null) return;
            await Run(async () =>
            {
                Exception error = null;
                try { await Task.Run(action); AddLog(success); }
                catch (Exception ex) { error = ex; }
                try { ShowInspection(await Task.Run(() => engine.Inspect())); }
                catch (Exception ex) { inspection = null; if (error == null) error = ex; }
                if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            });
        }

        private async Task Run(Func<Task> work)
        {
            if (busy) return;
            busy = true;
            RefreshButtons();
            UseWaitCursor = true;
            try { await work(); }
            catch (Exception ex)
            {
                status.Text = "需要处理后重试";
                status.ForeColor = Color.FromArgb(163, 77, 21);
                detail.Text = ex is UnauthorizedAccessException ? "目录没有写入权限。请检查文件夹权限，必要时以管理员身份运行。" : ex.Message.Split('\r', '\n')[0];
                AddLog(ex.Message);
                MessageBox.Show(this, detail.Text + "\r\n\r\n详细信息见下方日志。", "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { busy = false; UseWaitCursor = false; RefreshButtons(); }
        }

        private void ShowInspection(Inspection found)
        {
            inspection = found;
            string selected = (languages.SelectedItem as LanguageItem ?? new LanguageItem("EN.zip")).File;
            languages.Items.Clear();
            foreach (string file in found.Languages) languages.Items.Add(new LanguageItem(file));
            LanguageItem choice = languages.Items.Cast<LanguageItem>().FirstOrDefault(l => l.File == (found.ActiveLanguage ?? selected));
            if (choice != null) languages.SelectedItem = choice;
            else if (languages.Items.Count > 0) languages.SelectedIndex = 0;
            status.ForeColor = found.Problem == null ? Accent : Color.FromArgb(163, 77, 21);
            status.Text = found.Pending ? "检测到未完成的文件名交换" : found.Problem != null ? "文件校验需要处理" :
                found.ActiveLanguage != null ? "已启用：" + new LanguageItem(found.ActiveLanguage) + " → 中文字符表" : "准备就绪 · 当前没有启用交换";
            detail.Text = found.Problem ?? (found.BackupCount == 0 ? "首次交换时自动备份。请确保当前语言包为原始文件；程序无法自行识别包内的语言。" :
                "已校验 " + found.BackupCount + " 份备份及当前文件。切换其他语言时，会同步还原上一组文件名。");
            if (found.Problem != null) AddLog(found.Problem);
            UpdatePreview();
        }

        private void RefreshButtons()
        {
            foreach (Control control in interactive) control.Enabled = !busy;
            bool ready = !busy && engine != null && inspection != null;
            apply.Enabled = ready && inspection.Problem == null && languages.SelectedItem != null;
            restore.Enabled = ready && inspection.Problem == null && inspection.ActiveLanguage != null;
            recover.Enabled = ready && inspection.Pending;
            baseline.Enabled = ready && !inspection.Pending;
        }

        private void UpdatePreview()
        {
            LanguageItem item = languages.SelectedItem as LanguageItem;
            preview.Text = item == null ? "检查目录后选择语言" : "CHS.zip  ↔  " + item.File + "\r\n仅交换文件名，保留包内内容";
        }

        private void AddLog(string message) { log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine); }

        private static Panel Card() { return new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) }; }
        private static Label LabelAt(string text, int x, int y, int width, int height, float size, Color color, FontStyle style)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height),
                ForeColor = color, Font = new Font("Microsoft YaHei UI", size, style) };
        }
        private static Button MakeButton(string text, bool primary) { var button = new Button(); StyleButton(button, text, primary); return button; }
        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 0, 3, 10);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(204, 214, 224);
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            button.Cursor = Cursors.Hand;
        }

        private static string FindGame()
        {
            var roots = new List<string>();
            if (Directory.Exists(DefaultPath)) return DefaultPath;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string steam = key == null ? null : key.GetValue("SteamPath") as string;
                if (steam != null)
                {
                    roots.Add(steam);
                    string libraries = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(libraries))
                        foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
                            roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            foreach (string drive in Directory.GetLogicalDrives())
            {
                roots.Add(Path.Combine(drive, "SteamLibrary"));
                roots.Add(Path.Combine(drive, @"Program Files (x86)\Steam"));
                roots.Add(Path.Combine(drive, @"Program Files\Steam"));
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(root => Path.Combine(root, @"steamapps\common\ForzaHorizon6"))
                .FirstOrDefault(root => File.Exists(Path.Combine(root, @"media\Stripped\StringTables\CHS.zip")));
        }

        private sealed class LanguageItem
        {
            public readonly string File;
            public LanguageItem(string file) { File = file; }
            public override string ToString()
            {
                string code = Path.GetFileNameWithoutExtension(File).ToUpperInvariant();
                var names = new Dictionary<string, string> { { "EN", "英语" }, { "GB", "英语（英国）" }, { "JP", "日语" },
                    { "DE", "德语" }, { "FR", "法语" }, { "ES", "西班牙语" }, { "MX", "西班牙语（墨西哥）" },
                    { "IT", "意大利语" }, { "KO", "韩语" }, { "CHT", "繁体中文" }, { "BR", "葡萄牙语（巴西）" },
                    { "PT", "葡萄牙语" }, { "RU", "俄语" }, { "PL", "波兰语" }, { "TR", "土耳其语" },
                    { "CZ", "捷克语" }, { "DK", "丹麦语" }, { "EL", "希腊语" }, { "FI", "芬兰语" },
                    { "HU", "匈牙利语" }, { "NL", "荷兰语" }, { "NO", "挪威语" }, { "SV", "瑞典语" } };
                return (names.ContainsKey(code) ? names[code] : code) + "  ·  " + code;
            }
        }
    }
}
