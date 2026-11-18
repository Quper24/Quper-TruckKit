using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuperDesk {
    internal static class Program {
        [STAThread]
        private static void Main() {
            EnableLongPaths();
            L.Initialize();
            var app = new Application();
            using (var theme = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.Theme"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(theme));
            app.Run(new MainWindow());
        }

        internal static void EnableLongPaths() {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }
    }

    internal sealed partial class MainWindow : Window {
        private const string BoostyUrl = "https://boosty.to/qupersimulator";
        private const string FeedbackUrl = "https://forum.scssoft.com/viewtopic.php?t=355198";
        private static readonly Brush Ink = BrushOf("#EDF2E8"), Muted = BrushOf("#A1AEA0"), Accent = BrushOf("#B8E197"),
            Line = BrushOf("#303B30"), Warn = BrushOf("#F4BE8C"), Surface = BrushOf("#1A231B"), Dark = BrushOf("#111812");
        private readonly Grid shell = new Grid { Background = Dark };
        private readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly List<Button> nav = new List<Button>();
        private readonly TextBlock status = new TextBlock(), versionBadge = new TextBlock();
        private Button ets, ats, backupButton;
        private int page;
        private bool busy, fetching;
        private string backup;
        private CatalogOrigin catalogSource;
        private Catalog catalog;
        private readonly Game[] games = { Game.Create(false), Game.Create(true) };
        private int gameIndex;
        private string preferredProfile, preferredCombo;
        private Game Current { get { return games[gameIndex]; } }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public MainWindow() {
            Title = "Quper TruckKit";
            Width = 1150; Height = 800; MinWidth = 940; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14; Foreground = Ink; Background = Dark;
            UseLayoutRounding = true;
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
            SourceInitialized += (s, e) => { int on = 1; try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); } catch (EntryPointNotFoundException) { } };
            using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.Icon")) {
                if (icon != null) Icon = new IconBitmapDecoder(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(207) });
            shell.ColumnDefinitions.Add(new ColumnDefinition()); Content = shell;
            BuildSidebar(); BuildWorkspace();
            Closing += (s, e) => { if (busy) e.Cancel = true; };
            catalog = Catalog.Local(out catalogSource);
            ShowPage(0);
            Loaded += async (s, e) => await RefreshCatalog(false);
        }
        private static Brush BrushOf(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color); b.Freeze(); return b; }
        private static TextBlock Text(string value, double size = 14, Brush color = null, bool bold = false) {
            return new TextBlock { Text = value, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }
        private Button Button(string label, Action click, string style = null) {
            var b = new Button { Content = label, Margin = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
            if (style != null) b.Style = (Style)FindResource(style);
            b.Click += (s, e) => { try { click(); } catch (Exception err) { SetStatus(err.Message, Warn); } };
            return b;
        }
        private static StackPanel Stack(bool horizontal = false) { return new StackPanel { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical }; }
        private static void Add(Panel parent, FrameworkElement element, int top = 0, int bottom = 0) {
            element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, bottom); parent.Children.Add(element);
        }
        private static Border Box(UIElement child, int pad = 22) {
            return new Border { Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(pad), Child = child };
        }
        private static void Pair(Panel parent, FrameworkElement left, FrameworkElement right, int top = 0) {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right); Add(parent, grid, top);
        }
        private static FrameworkElement Field(string caption, FrameworkElement control) {
            var p = Stack(); Add(p, Text(caption, 12, Muted, true), 0, 6); Add(p, control);
            AutomationProperties.SetName(control, caption); return p;
        }
        private static ComboBox MakeDropdown() { return new ComboBox { IsTextSearchEnabled = true }; }
        private static void Bind<T>(ComboBox control, IList<T> items, int selected = 0) {
            control.ItemsSource = items; control.IsEnabled = items.Count > 0; control.SelectedIndex = items.Count > 0 ? Math.Max(0, Math.Min(selected, items.Count - 1)) : -1;
        }
        private void BuildSidebar() {
            var side = new DockPanel { LastChildFill = true };
            var panel = new Border { Background = BrushOf("#151E17"), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
            shell.Children.Add(panel);
            var footer = Stack(); footer.Margin = new Thickness(15, 0, 15, 20);
            Add(footer, FooterLink("qupersimulator.ru  ↗", "https://qupersimulator.ru/"));
            Add(footer, FooterLink(L.T("Оставить отзыв  ↗"), FeedbackUrl, L.T("Оставьте отзыв в теме приложения на форуме SCS.")));
            Add(footer, FooterLink(L.T("Поддержать автора  ↗"), BoostyUrl + "/donate"));
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var release = Text("DESKTOP  /  " + version, 10, Muted, true); release.Margin = new Thickness(15, 0, 0, 0); Add(footer, release, 6);
            DockPanel.SetDock(footer, Dock.Bottom); side.Children.Add(footer);
            var menu = Stack(); menu.Margin = new Thickness(13, 17, 13, 0);
            side.Children.Add(new ScrollViewer { Content = menu, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var logo = new BitmapImage();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.Logo")) {
                logo.BeginInit(); logo.CacheOption = BitmapCacheOption.OnLoad; logo.DecodePixelWidth = 320; logo.StreamSource = stream; logo.EndInit();
            }
            var brand = new Image { Source = logo, Width = 133, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(brand, "Quper TruckKit"); Add(menu, brand, 10, 30);
            var label = Text(L.T("ИНСТРУМЕНТЫ"), 10, Muted, true); label.Margin = new Thickness(13, 0, 0, 0); Add(menu, label, 0, 10);
            var labels = new[] { L.T("Клонирование профиля"), L.T("Грузовики"), L.T("Деньги и опыт"), L.T("Сборки и карты"), L.T("Управление"), L.T("Как пользоваться"), L.T("Копирование модов"), L.T("Менеджер модов"), L.T("Настройки конфига") };
            var icons = new[] { "\uE77B", "\uE806", "\uE9D9", "\uE707", "\uE7FC", "\uE897", "\uE8C8", "\uE8FD", "\uE713" };
            foreach (var i in new[] { 0, 6, 7, 1, 2, 3, 4, 8, 5 }) {
                var index = i; var b = Button(labels[i], () => ShowPage(index), "Quiet");
                var line = Stack(true); var glyph = Text(icons[i], 15, Accent); glyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
                glyph.Margin = new Thickness(0, 2, 9, 0); glyph.Width = 19; line.Children.Add(glyph);
                var text = Text(labels[i], 13, Ink, true); line.Children.Add(text); b.Content = line; AutomationProperties.SetName(b, labels[i]);
                b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(12, 11, 7, 11);
                while (nav.Count <= i) nav.Add(null);
                nav[i] = b; Add(menu, b, 4);
            }
        }
        private Button FooterLink(string label, string url, string tip = null) {
            var link = Button(label, () => Open(url), "Quiet");
            link.HorizontalContentAlignment = HorizontalAlignment.Left;
            link.Padding = new Thickness(12, 5, 4, 5);
            link.MinHeight = 29;
            link.ToolTip = tip ?? url;
            return link;
        }
        private void BuildWorkspace() {
            var work = new DockPanel { LastChildFill = true }; Grid.SetColumn(work, 1); shell.Children.Add(work);
            var top = new DockPanel { LastChildFill = true, Margin = new Thickness(26, 12, 25, 12) };
            var controls = Stack(true);
            ets = Button("ETS2", () => SwitchGame(0)); ats = Button("ATS", () => SwitchGame(1));
            controls.Children.Add(ets); ats.Margin = new Thickness(5, 0, 0, 0); controls.Children.Add(ats);
            var folder = Button(L.T("Папка игры…"), ChooseFolder); folder.Margin = new Thickness(14, 0, 0, 0); controls.Children.Add(folder);
            var reload = Button("↻", () => { ShowPage(page); SetStatus(L.T("Список профилей и сохранений обновлён."), Accent); });
            reload.ToolTip = L.T("Обновить профили и сохранения"); reload.Margin = new Thickness(6, 0, 0, 0); controls.Children.Add(reload);
            var language = MakeDropdown();
            language.Width = 67;
            language.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(language, L.T("Язык интерфейса"));
            Bind(language, new[] { "RU", "EN" }, L.Language == "ru" ? 0 : 1);
            language.SelectionChanged += (sender, e) => {
                if (language.SelectedIndex < 0) return;
                try { ChangeLanguage(language.SelectedIndex == 0 ? "ru" : "en"); }
                catch (IOException) { SetStatus(L.T("Не удалось сохранить язык интерфейса."), Warn); }
                catch (UnauthorizedAccessException) { SetStatus(L.T("Не удалось сохранить язык интерфейса."), Warn); }
            };
            controls.Children.Add(language);
            DockPanel.SetDock(controls, Dock.Right); top.Children.Add(controls);
            var brand = Stack(); Add(brand, Text("Quper TruckKit", 12, Accent, true), 2); versionBadge.Foreground = Muted; versionBadge.FontSize = 12;
            versionBadge.TextTrimming = TextTrimming.CharacterEllipsis; Add(brand, versionBadge, 3); top.Children.Add(brand);
            var header = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = BrushOf("#151E17"), Child = top };
            DockPanel.SetDock(header, Dock.Top); work.Children.Add(header);
            var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(22, 6, 18, 6) };
            backupButton = Button(L.T("Резервная копия ↗"), () => Open(backup), "Quiet"); backupButton.MinHeight = 30; backupButton.Visibility = backup == null ? Visibility.Collapsed : Visibility.Visible;
            DockPanel.SetDock(backupButton, Dock.Right); bottom.Children.Add(backupButton);
            status.Foreground = Muted; status.FontSize = 12; status.TextWrapping = TextWrapping.Wrap; status.VerticalAlignment = VerticalAlignment.Center;
            status.MinHeight = 20; status.Text = L.T("Локальные профили  ·  Закройте игру перед изменениями."); bottom.Children.Add(status);
            var footer = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = BrushOf("#151E17"), Child = bottom };
            DockPanel.SetDock(footer, Dock.Bottom); work.Children.Add(footer); work.Children.Add(scroll);
        }
        internal void ChangeLanguage(string language, bool save = true) {
            if (language == L.Language) return;
            L.Select(language, save);
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
            foreach (var element in new FrameworkElement[] { scroll, status, versionBadge }) {
                var parent = element.Parent as Panel;
                if (parent != null) parent.Children.Remove(element);
            }
            shell.Children.Clear();
            nav.Clear();
            BuildSidebar();
            BuildWorkspace();
            ShowPage(page);
        }
        private void SwitchGame(int index) { if (gameIndex != index) { gameIndex = index; preferredProfile = null; ShowPage(page); SetStatus(L.T("Выбрана игра: ") + Current.Title + ".", Accent); } }
        private void ChooseFolder() {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Current.Root, Description = L.T("Папка «") + Current.Title + L.T("», в которой находятся profiles и mod"), ShowNewFolderButton = false }) {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!Directory.Exists(Path.Combine(dialog.SelectedPath, "profiles")) && !Directory.Exists(Path.Combine(dialog.SelectedPath, "steam_profiles")))
                    throw new InvalidOperationException(L.T("В выбранной папке нет profiles или steam_profiles."));
                Current.Root = dialog.SelectedPath; preferredProfile = null; ShowPage(page); SetStatus(L.T("Папка игры обновлена: ") + Current.Root, Accent);
            }
        }
        private void SetStatus(string value, Brush color = null, string newBackup = null) {
            status.Text = value; status.Foreground = color ?? Muted;
            if (newBackup != null) { backup = newBackup; backupButton.Visibility = Visibility.Visible; }
        }
        private static void Open(string path) { if (!string.IsNullOrEmpty(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        private void ShowPage(int index) {
            page = index;
            for (var i = 0; i < nav.Count; i++) { nav[i].Background = i == index ? BrushOf("#283B28") : Brushes.Transparent; nav[i].BorderBrush = i == index ? BrushOf("#456044") : Brushes.Transparent; }
            ets.Background = gameIndex == 0 ? Accent : Surface; ets.Foreground = gameIndex == 0 ? Dark : Ink;
            ats.Background = gameIndex == 1 ? Accent : Surface; ats.Foreground = gameIndex == 1 ? Dark : Ink;
            var ver = Current.DetectedVersion(); versionBadge.Text = Current.Title + (ver == null ? "" : "  ·  " + ver); versionBadge.ToolTip = Current.Root;
            var content = Stack(); content.Margin = new Thickness(26, 22, 25, 28); scroll.Content = content; scroll.ScrollToTop();
            try {
                var profiles = index == 5 || index == 8 ? new List<Profile>() : Current.Profiles();
                switch (index) {
                    case 0: ProfilesPage(content, profiles); break;
                    case 1: TrucksPage(content, profiles); break;
                    case 2: ProgressPage(content, profiles); break;
                    case 3: ModsPage(content, profiles); break;
                    case 4: ControlsPage(content, profiles); break;
                    case 6: CopyModsPage(content, profiles); break;
                    case 7: ModManagerPage(content, profiles); break;
                    case 8: ConfigPage(content); break;
                    default: HelpPage(content); break;
                }
            } catch (Exception e) { Add(content, Box(Text(e.Message, 14, Warn)), 12); }
        }
        private static void Hero(Panel p, string label, string title, string description, string symbol) {
            var stack = Stack(); Add(stack, Text(label, 11, Accent, true), 0, 7);
            Add(stack, Text(title, 32, Ink, true)); Add(stack, Text(description, 14, Muted), 7);
            var grid = new Grid();
            var icon = Text(symbol, 88, BrushOf("#466244")); icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.HorizontalAlignment = HorizontalAlignment.Right; icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 23, 0);
            grid.Children.Add(icon); stack.Margin = new Thickness(0, 0, 115, 0); grid.Children.Add(stack);
            var hero = Box(grid, 22);
            hero.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#253726"), (Color)ColorConverter.ConvertFromString("#1B291E"), 10);
            Add(p, hero, 0, 17);
        }
        private static void Step(Panel p, string number, string title, string detail) {
            var dock = new DockPanel(); var badge = Text(number, 13, Accent, true);
            var bg = new Border { Background = BrushOf("#2B3E2B"), CornerRadius = new CornerRadius(8), Child = badge, Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 11, 0) };
            DockPanel.SetDock(bg, Dock.Left); dock.Children.Add(bg);
            var words = Stack(); Add(words, Text(title, 14, Ink, true)); Add(words, Text(detail, 13, Muted), 4); dock.Children.Add(words); Add(p, dock, 13);
        }
        private bool NoProfiles(Panel p, IList<Profile> profiles) {
            if (profiles.Count > 0) return false;
            var empty = Stack(); Add(empty, Text(L.T("Локальные профили не найдены"), 18, Ink, true));
            Add(empty, Text(Current.HasCloud ? L.T("Найдены облачные профили. В игре откройте настройки профиля, снимите «Использовать Steam Cloud», загрузите профиль и сделайте сохранение. Затем закройте игру и нажмите ↻.") : L.T("Создайте профиль и сохраните игру, затем закройте её. Если папка игры нестандартная, выберите её кнопкой «Папка игры». После этого нажмите ↻."), 14, Muted), 9);
            Add(p, Box(empty)); return true;
        }
        private ComboBox ProfileDrop(IList<Profile> profiles) {
            var drop = MakeDropdown(); var i = profiles.ToList().FindIndex(p => p.Name == preferredProfile); Bind(drop, profiles, i); return drop;
        }
        private sealed class SlotPicker {
            public readonly ComboBox Profile, Save;
            public readonly StackPanel Form;
            public SaveSlot Selected { get { return Save.SelectedItem as SaveSlot; } }
            public SlotPicker(ComboBox profile, ComboBox save, StackPanel form) { Profile = profile; Save = save; Form = form; }
        }
        private SlotPicker Picker(IList<Profile> profiles, string caption, int start = -1) {
            var form = Stack(); var profile = ProfileDrop(profiles); var save = MakeDropdown();
            if (start >= 0 && start < profiles.Count) profile.SelectedIndex = start;
            Add(form, Field(caption, profile)); Add(form, Field(L.T("Сохранение"), save), 10);
            Action change = () => { var selected = profile.SelectedItem as Profile; Bind(save, selected == null ? new List<SaveSlot>() : selected.Saves()); };
            profile.SelectionChanged += (s, e) => { try { change(); } catch (Exception error) { Bind(save, new List<SaveSlot>()); SetStatus(error.Message, Warn); } };
            change(); return new SlotPicker(profile, save, form);
        }
        private bool Confirm(string title, string detail, string action) {
            var dialog = new Window { Owner = this, Title = title, Width = 490, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Surface, Foreground = Ink, FontFamily = FontFamily, FontSize = FontSize };
            var form = Stack(); form.Margin = new Thickness(23); Add(form, Text(title, 22, Ink, true)); Add(form, Text(detail, 14, Muted), 10);
            var row = Stack(true); row.HorizontalAlignment = HorizontalAlignment.Right;
            var cancel = Button(L.T("Отмена"), () => dialog.DialogResult = false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(cancel); var ok = Button(action, () => dialog.DialogResult = true, "Primary"); ok.IsDefault = true; row.Children.Add(ok); Add(form, row, 20);
            dialog.Content = form; return dialog.ShowDialog() == true;
        }
        private async Task Work(string working, Func<string> action, Action<string> done) {
            if (busy) return;
            busy = true; shell.IsEnabled = false; SetStatus(working, Accent);
            try { var result = await Task.Run(action); done(result); }
            catch (Exception e) { SetStatus(e.Message, Warn); }
            finally { shell.IsEnabled = true; busy = false; }
        }
        private void ProfilesPage(Panel p, IList<Profile> profiles) {
            Hero(p, L.T("01   /   ПРОФИЛИ"), L.T("Клонирование профиля"), L.T("Создайте отдельный профиль с теми же сохранениями и настройками."), "\uE8C8");
            if (NoProfiles(p, profiles)) return;
            var panel = Stack(); Add(panel, Text(L.T("Новая копия"), 19, Ink, true));
            var source = ProfileDrop(profiles); var name = new TextBox { MaxLength = 32 };
            Pair(panel, Field(L.T("Исходный профиль"), source), Field(L.T("Имя копии"), name), 14);
            var action = Button(L.T("Клонировать профиль  →"), () => { }, "Primary"); action.HorizontalAlignment = HorizontalAlignment.Left; action.IsEnabled = false;
            name.TextChanged += (s, e) => action.IsEnabled = name.Text.Trim().Length >= 2;
            action.Click += async (s, e) => {
                var profile = (Profile)source.SelectedItem; var target = name.Text.Trim();
                await Work(L.T("Копирую профиль и сохранения…"), () => profile.Clone(target), result => { preferredProfile = target; ShowPage(0); SetStatus(L.T("Профиль «") + target + L.T("» создан. Выберите его в меню игры."), Accent); });
            };
            Add(panel, action, 16); Add(p, Box(panel));
            var guide = Stack(); Add(guide, Text(L.T("Как это сделать"), 17, Ink, true));
            Step(guide, "1", L.T("Закройте игру"), L.T("Убедитесь, что нужный профиль сохранён на компьютере."));
            Step(guide, "2", L.T("Выберите профиль и придумайте имя"), L.T("Нажмите «Клонировать профиль», чтобы создать новый профиль с теми же сохранениями."));
            Step(guide, "3", L.T("Откройте игру"), L.T("Выберите новое имя в списке профилей и загрузите нужное сохранение.")); Add(p, Box(guide), 14);
        }
        private static string Format(long n) { return n.ToString("N0", L.Culture); }
        private static bool Delta(string raw, out long n) {
            var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
            n = 0; return cleaned.Length == 0 || cleaned.All(c => c >= '0' && c <= '9') && long.TryParse(cleaned, out n);
        }
        private void ProgressPage(Panel p, IList<Profile> profiles) {
            Hero(p, L.T("03   /   КАРЬЕРА"), L.T("Деньги и опыт"), L.T("Измените деньги и прогресс карьеры в выбранном сохранении."), "\uE9D9");
            if (NoProfiles(p, profiles)) return;
            var ats = Current.Code == "ats";
            var form = Stack(); var pick = Picker(profiles, L.T("Профиль")); Add(form, pick.Form);
            var state = Text("", 14, Accent, true); Add(form, state, 14);
            var money = new TextBox { MaxLength = 25 }; var xp = new TextBox { MaxLength = 16 };
            Pair(form, Field(L.T("Добавить денег"), money), Field(L.T("Добавить опыта грузовиков (XP)"), xp), 14);
            var truckNote = Text("", 13, Muted);
            Add(form, truckNote, 6);
            var coach = new TextBox { MaxLength = 16, IsEnabled = false };
            var rating = SettingsChoice(L.T("Поднять оценки завершённых доставок до 5 звёзд"));
            rating.IsEnabled = false;
            var extraNote = Text("", 13, Muted);
            if (ats) {
                Add(form, Text(L.T("Road Trip · рейтинг курьера"), 14, Ink, true), 16);
                Add(form, rating, 8);
            } else {
                Add(form, Field(L.T("Добавить опыта автобусов Coaches (XP)"), coach), 14);
            }
            Add(form, extraNote, 6);
            var summary = Text("", 13, Muted); Add(form, summary, 10);
            var action = Button(L.T("Добавить в сохранение  →"), () => { }, "Primary"); action.HorizontalAlignment = HorizontalAlignment.Left; Add(form, action, 14);
            CareerProgress values = null; int serial = 0;
            Action preview = () => {
                long m, x, c;
                var valid = Delta(money.Text, out m) & Delta(xp.Text, out x) & Delta(coach.Text, out c);
                var raise = rating.IsChecked == true;
                action.IsEnabled = values != null && valid && (m > 0 || x > 0 || c > 0 || raise) &&
                    (x == 0 || values.CanAddTruckXp) && (c == 0 || values.CanAddCoachXp) &&
                    values.Fits(m, x, c) && (!raise || values.RatingsToRaise > 0);
                if (!valid) summary.Text = L.T("Введите целое положительное число.");
                else if (values == null || m == 0 && x == 0 && c == 0 && !raise) summary.Text = L.T("Оставьте пустыми поля, которые хотите сохранить как есть.");
                else if (!action.IsEnabled) summary.Text = L.T("Итоговое значение превышает предел игры.");
                else {
                    var changes = new List<string>();
                    if (m > 0) changes.Add(L.T("деньги ") + Format(values.Money + m));
                    if (x > 0) changes.Add(L.T("грузовики ") + Format(values.TruckXp + x) + " XP");
                    if (c > 0) changes.Add("Coaches " + Format(values.CoachXp.Value + c) + " XP");
                    if (raise) changes.Add(L.T("оценка 5 звёзд у доставок: ") + values.RatingsToRaise);
                    summary.Text = L.T("После записи: ") + string.Join("  ·  ", changes);
                }
            };
            Func<bool, Task> load = async resetExtras => {
                var rev = ++serial; var slot = pick.Selected; values = null;
                if (resetExtras) { coach.Clear(); rating.IsChecked = false; }
                xp.IsEnabled = false; coach.IsEnabled = false; rating.IsEnabled = false; truckNote.Text = ""; extraNote.Text = ""; preview();
                state.Text = slot == null ? L.T("Сохранений нет. Сделайте ручное сохранение в игре.") : L.T("Читаю сохранение…");
                try {
                    if (slot == null) return;
                    var found = await Task.Run(() => slot.Progress());
                    if (rev != serial) return;
                    values = found;
                    state.Text = L.T("Деньги: ") + Format(found.Money) + L.T("  ·  Грузовики: ") + Format(found.TruckXp) + " XP";
                    xp.IsEnabled = found.CanAddTruckXp;
                    if (!xp.IsEnabled) xp.Clear();
                    truckNote.Text = xp.IsEnabled ? "" : L.T("Опыт грузовиков доступен с 1-го уровня (200 XP). Получите уровень в игре и выберите новое сохранение.");
                    truckNote.Visibility = xp.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
                    coach.IsEnabled = found.CanAddCoachXp;
                    if (!coach.IsEnabled) coach.Clear();
                    if (found.CoachXp.HasValue) state.Text += "  ·  Coaches: " + Format(found.CoachXp.Value) + " XP";
                    if (ats) {
                        rating.IsEnabled = found.RatingsToRaise > 0;
                        if (!rating.IsEnabled) rating.IsChecked = false;
                        extraNote.Text = !found.HasCourierLog ? L.T("В сохранении нет поддерживаемого журнала Road Trip.")
                            : found.FinishedDeliveries == 0 ? L.T("Сначала завершите доставку в Road Trip и сохраните игру.")
                            : found.RatingsToRaise == 0 ? L.T("У всех завершённых доставок уже 5 звёзд.")
                            : L.T("Доставок с оценкой ниже 5 звёзд: ") + found.RatingsToRaise + L.T(". Игра использует оценки доставок для рейтинга курьера.");
                    } else if (!found.CoachXp.HasValue) {
                        coach.Clear();
                        extraNote.Text = L.T("В сохранении нет данных Coaches. Откройте профиль в новой версии игры и сохранитесь заново.");
                    } else if (!coach.IsEnabled) extraNote.Text = L.T("Опыт Coaches доступен с 1-го уровня автобусов (200 XP). Получите уровень в игре и выберите новое сохранение.");
                    preview();
                }
                catch (Exception e) { if (rev == serial) state.Text = e.Message; }
            };
            pick.Save.SelectionChanged += async (s, e) => await load(true);
            money.TextChanged += (s, e) => preview(); xp.TextChanged += (s, e) => preview(); coach.TextChanged += (s, e) => preview();
            rating.Checked += (s, e) => preview(); rating.Unchecked += (s, e) => preview();
            action.Click += async (s, e) => {
                long m, x, c;
                if (!Delta(money.Text, out m) || !Delta(xp.Text, out x) || !Delta(coach.Text, out c) || values == null) return;
                var slot = pick.Selected; var expected = values;
                var raise = rating.IsChecked == true;
                await Work(L.T("Сохраняю изменения карьеры…"), () => slot.AddProgress(m, x, c, raise, expected), result => {
                    SetStatus(L.T("Изменения сохранены. Загрузите это сохранение в игре."), Accent, result);
                    money.Clear(); xp.Clear(); coach.Clear(); rating.IsChecked = false;
                });
                await load(false);
            };
            Add(p, Box(form)); var hint = Text(L.T("Совет: используйте ручное сохранение, чтобы игра не перезаписала его следующим автосохранением."), 13, Muted);
            Add(p, hint, 14); var ignore = load(true);
        }
        private void TrucksPage(Panel p, IList<Profile> profiles) {
            Hero(p, L.T("02   /   АВТОПАРК"), L.T("Перенос грузовиков"), L.T("Скопируйте выбранные тягачи с тюнингом в гараж другого профиля."), "\uE806");
            if (NoProfiles(p, profiles)) return;
            if (profiles.Count < 2) {
                var message = Stack(); Add(message, Text(L.T("Для переноса нужны два локальных профиля"), 18, Ink, true));
                Add(message, Text(L.T("Создайте ещё один профиль в игре или сделайте копию на вкладке «Профили». Перед переносом сделайте в обоих профилях ручные сохранения."), 14, Muted), 9);
                var go = Button(L.T("Открыть профили"), () => ShowPage(0)); go.HorizontalAlignment = HorizontalAlignment.Left; Add(message, go, 13); Add(p, Box(message)); return;
            }
            var left = Stack(); var right = Stack(); var source = Picker(profiles, L.T("Исходный профиль"), 0); var target = Picker(profiles, L.T("Профиль назначения"), 1);
            Add(left, Text(L.T("Откуда"), 18, Ink, true), 0, 12); Add(left, source.Form);
            Add(right, Text(L.T("Куда"), 18, Ink, true), 0, 12); Add(right, target.Form);
            var list = new ListBox { Height = 157, SelectionMode = SelectionMode.Multiple }; Add(left, Field(L.T("Грузовики · можно выбрать несколько"), list), 12);
            var garage = MakeDropdown(); Add(right, Field(L.T("Гараж назначения"), garage), 12);
            var warning = Text("", 13, Warn, true); Add(right, warning, 9);
            var steps = Stack();
            Step(steps, "1", L.T("Откройте профиль назначения в игре"), L.T("В менеджере гаражей найдите гараж со свободным слотом или освободите место: переместите грузовик в другой гараж либо расширьте гараж."));
            Step(steps, "2", L.T("Сделайте ручное сохранение и закройте игру"), L.T("В приложении нажмите ↻ вверху окна. Затем выберите новое сохранение и нужный гараж."));
            steps.Visibility = Visibility.Collapsed; Add(right, steps);
            Pair(p, Box(left, 16), Box(right, 16));
            var row = Stack(); var action = Button(L.T("Скопировать грузовики  →"), () => { }, "Primary"); action.HorizontalAlignment = HorizontalAlignment.Left;
            Add(row, Text(L.T("В профиле назначения должны быть доступны моды и DLC выбранных грузовиков."), 13, Muted)); Add(row, action, 10); Add(p, row, 13);
            int sourceSerial = 0, targetSerial = 0; bool loading = true; string error = null;
            var available = new List<Garage>();
            Action show = () => {
                var g = garage.SelectedItem as Garage; var count = list.SelectedItems.Count;
                var same = source.Selected != null && target.Selected != null && source.Selected.Profile.DirectoryPath == target.Selected.Profile.DirectoryPath;
                var noSpace = !loading && error == null && target.Selected != null && (g == null || g.Free == 0 || g.Free < count);
                steps.Visibility = noSpace ? Visibility.Visible : Visibility.Collapsed;
                warning.Text = loading ? L.T("Читаю гаражи…") : error ?? (noSpace ? Trucks.NoSpace : same ? L.T("Выберите другой профиль назначения.") : target.Selected == null ? L.T("Сделайте сохранение в профиле назначения.") : L.T("Свободных слотов: ") + g.Free);
                warning.Foreground = noSpace || same || error != null ? Warn : Muted;
                action.IsEnabled = !loading && error == null && !same && count > 0 && source.Selected != null && target.Selected != null && g != null && g.Free >= count;
            };
            Func<Task> loadSource = async () => {
                var rev = ++sourceSerial; var slot = source.Selected;
                list.ItemsSource = new[] { slot == null ? L.T("Сохранений нет.") : L.T("Читаю грузовики…") }; list.IsEnabled = false; show();
                try {
                    if (slot == null) return;
                    var trucks = await Task.Run(() => Trucks.List(slot));
                    if (rev != sourceSerial) return;
                    if (trucks.Count == 0) { list.ItemsSource = new[] { L.T("В сохранении нет купленных грузовиков.") }; return; }
                    list.ItemsSource = trucks; list.IsEnabled = true;
                } catch (Exception e) { if (rev == sourceSerial) list.ItemsSource = new[] { e.Message }; }
                finally { if (rev == sourceSerial) show(); }
            };
            Func<Task> loadTarget = async () => {
                var rev = ++targetSerial; var slot = target.Selected; loading = true; error = null; Bind(garage, new List<Garage>()); show();
                try {
                    var found = slot == null ? new List<Garage>() : await Task.Run(() => Trucks.Garages(slot));
                    if (rev == targetSerial) { available = found; Bind(garage, available); }
                } catch (Exception e) { if (rev == targetSerial) error = e.Message; }
                finally { if (rev == targetSerial) { loading = false; show(); } }
            };
            source.Save.SelectionChanged += async (s, e) => await loadSource(); target.Save.SelectionChanged += async (s, e) => await loadTarget();
            garage.SelectionChanged += (s, e) => show(); list.SelectionChanged += (s, e) => show();
            action.Click += async (s, e) => {
                var from = source.Selected; var to = target.Selected; var g = (Garage)garage.SelectedItem;
                var ids = list.SelectedItems.Cast<Truck>().Select(t => t.Id).ToList();
                if (!Confirm(L.T("Скопировать в гараж?"), L.T("Профиль: «") + to.Profile.Name + L.T("». Гараж: ") + g.City + L.T(". Грузовиков: ") + ids.Count + L.T(".\n\nОткройте это сохранение в игре после завершения."), L.T("Скопировать"))) return;
                await Work(L.T("Сохраняю грузовики…"), () => Trucks.Copy(from, to, ids, g.Id), result => SetStatus(L.T("Грузовики добавлены в гараж «") + g.City + L.T("». Загрузите сохранение в игре."), Accent, result));
                list.UnselectAll(); await loadTarget();
            };
            var a = loadSource(); var b = loadTarget();
        }
        private async Task RefreshCatalog(bool explicitRequest) {
            if (fetching) return;
            fetching = true;
            if (explicitRequest) SetStatus(L.T("Проверяю каталог сборок…"), Accent);
            try {
                var loaded = await Task.Run(() => Catalog.Fetch());
                var old = catalog.combos.ToDictionary(c => c.id);
                var newCount = loaded.combos.Count(c => !old.ContainsKey(c.id));
                var changedCount = loaded.combos.Count(c => {
                    Combo before;
                    return old.TryGetValue(c.id, out before) && (before.release != c.release || before.title != c.title ||
                        !before.mods.Select(m => Tuple.Create(m.key, m.name, m.filename)).SequenceEqual(c.mods.Select(m => Tuple.Create(m.key, m.name, m.filename))));
                });
                catalog = loaded; catalogSource = new CatalogOrigin(false, DateTime.Now);
                if (page == 3 && !busy) ShowPage(3);
                if (newCount + changedCount > 0) SetStatus(L.T("Каталог сайта: новых сборок — ") + newCount + L.T(", обновлённых — ") + changedCount + ".", Accent);
                else if (explicitRequest) SetStatus(L.T("Каталог обновлён с сайта."), Accent);
            } catch (Exception) { if (explicitRequest) SetStatus(L.T("Сайт не отдал каталог. Доступна последняя сохранённая копия."), Warn); }
            finally { fetching = false; }
        }
        private void ModsPage(Panel p, IList<Profile> profiles) {
            Hero(p, L.T("04   /   КАТАЛОГ"), L.T("Сборки и карты"), L.T("Выберите сборку из каталога и примените её порядок к своему профилю."), "\uE707");
            var current = Current;
            var toolbar = new DockPanel();
            var sync = Button(L.T("Обновить с сайта  ↻"), () => { }); sync.Click += async (s, e) => await RefreshCatalog(true);
            DockPanel.SetDock(sync, Dock.Right); toolbar.Children.Add(sync);
            var caption = Text(catalogSource.Label, 13, Muted); caption.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(caption);
            Add(p, toolbar, 0, 12);
            var combos = catalog.combos.Where(c => c.game == current.Code).ToList();
            var choice = MakeDropdown(); Bind(choice, combos, combos.FindIndex(c => c.id == preferredCombo));
            var profile = ProfileDrop(profiles);
            var form = Stack(); Pair(form, Field(L.T("Сборка из каталога"), choice), Field(L.T("Профиль для сборки"), profile));
            var stats = Text("", 13, Accent, true); Add(form, stats, 12);
            var requirements = Text("", 12, Warn); Add(form, requirements, 5);
            var list = new ListBox { Height = 168, Focusable = false }; Add(form, Field(L.T("Порядок загрузки · высокий приоритет сверху"), list), 9);
            var note = Text("", 13, Muted); Add(form, note, 10);
            var buttons = Stack(true);
            var apply = Button(L.T("Применить порядок  →"), () => { }, "Primary");
            var website = Button(L.T("Страница сборки на сайте ↗"), () => Open(((Combo)choice.SelectedItem).page));
            website.Margin = new Thickness(9, 0, 0, 0); buttons.Children.Add(apply); buttons.Children.Add(website); Add(form, buttons, 12);
            var download = Button(L.T("Скачать сборки на Boosty  ↗"), () => Open(BoostyUrl), "Quiet");
            download.HorizontalAlignment = HorizontalAlignment.Left;
            download.Padding = new Thickness(0, 5, 8, 5);
            download.MinHeight = 30;
            Add(form, download, 8);
            string modWarning = null;
            Action preview = () => {
                var combo = choice.SelectedItem as Combo; var dest = profile.SelectedItem as Profile;
                apply.IsEnabled = combo != null && dest != null;
                website.IsEnabled = combo != null;
                website.ToolTip = combo == null ? null : combo.page;
                modWarning = null;
                if (combo == null) {
                    stats.Text = L.T("Для этой игры сборок в каталоге пока нет.");
                    requirements.Visibility = Visibility.Collapsed;
                    note.Text = "";
                    list.ItemsSource = null;
                    return;
                }
                preferredCombo = combo.id;
                stats.Text = combo.mods.Length + L.T(" файлов  ·  Игра ") + combo.version + (string.IsNullOrWhiteSpace(combo.release) ? "" : L.T("  ·  Сборка ") + combo.release);
                var dlcs = combo.dlcs ?? new string[0];
                var version = current.DetectedVersion();
                requirements.Text = (version == null || version == combo.version ? "" : L.T("Установлена версия игры ") + version + L.T(", а сборка для ") + combo.version + ". ") +
                    (dlcs.Length == 0 ? "" : L.T("Требуются DLC: ") + string.Join(", ", dlcs));
                requirements.Visibility = requirements.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                list.ItemsSource = null;
                try {
                    var inventory = new ModInventory(current.InstalledMods());
                    var checks = combo.mods.Reverse().Select(inventory.Inspect).ToList();
                    list.ItemsSource = checks.Select(ModOrderRow).ToList();
                    var counts = new List<string>();
                    var missing = checks.Count(check => check.Status == ModStatus.Missing);
                    var old = checks.Count(check => check.Status == ModStatus.Outdated);
                    var different = checks.Count(check => check.Status == ModStatus.Different);
                    if (missing > 0) counts.Add(L.T("Отсутствуют: ") + missing);
                    if (old > 0) counts.Add(L.T("Устарели: ") + old);
                    if (different > 0) counts.Add(L.T("Другая версия: ") + different);
                    if (counts.Count > 0) modWarning = string.Join("  ·  ", counts) + L.T(". Скачайте нужные версии перед загрузкой профиля в игре.");
                } catch (IOException) {
                    modWarning = L.T("Не удалось проверить папку mod. Проверьте наличие нужных файлов перед загрузкой профиля в игре.");
                } catch (UnauthorizedAccessException) {
                    modWarning = L.T("Нет доступа к папке mod. Проверьте наличие нужных файлов перед загрузкой профиля в игре.");
                }
                if (list.ItemsSource == null)
                    list.ItemsSource = combo.mods.Reverse().Select((mod, i) => Text((i + 1).ToString("00") + "   " + mod.name, 13)).ToList();
                note.Text = modWarning ?? (dest == null ? L.T("Выберите локальный профиль.") : L.T("Перед заменой списка активных модов будет создана резервная копия."));
                note.Foreground = modWarning == null ? Muted : Warn;
            };
            choice.SelectionChanged += (s, e) => preview(); profile.SelectionChanged += (s, e) => preview();
            apply.Click += async (s, e) => {
                var combo = (Combo)choice.SelectedItem; var dest = (Profile)profile.SelectedItem; var version = current.DetectedVersion();
                var mismatch = version != null && version != combo.version ? L.T("\n\nВнимание: установленная версия игры ") + version + L.T(", а сборка для ") + combo.version + "." : "";
                var warning = modWarning;
                var detail = string.Format(L.Culture, L.T("В профиле «{0}» будет установлен порядок «{1}» ({2} файлов). Перед записью будет создана резервная копия."), dest.Name, combo.DisplayTitle, combo.mods.Length);
                if (!Confirm(L.T("Применить сборку?"), detail
                    + mismatch + (warning == null ? "" : "\n\n" + warning), L.T("Применить"))) return;
                await Work(L.T("Записываю порядок модов…"), () => dest.ApplyMods(combo), result => SetStatus(
                    warning == null ? L.T("Порядок сохранён. Проверьте список в менеджере модов игры.") : L.T("Порядок сохранён. Проверьте отмеченные моды перед загрузкой профиля."), Accent, result));
            };
            preview(); Add(p, Box(form, 16));
        }

        private static FrameworkElement ModOrderRow(ModCheck check, int index) {
            var file = check.Mod.filename ?? check.Mod.key;
            var row = Stack();
            Add(row, Text((index + 1).ToString("00") + "   " + check.Mod.name, 13));
            string detail;
            switch (check.Status) {
                case ModStatus.Missing: detail = L.T("Отсутствует · скачайте ") + file; break;
                case ModStatus.Outdated: detail = L.T("Устарел · обновите до ") + file + L.T(" (найден ") + check.Installed + ")"; break;
                case ModStatus.Different: detail = L.T("Другая версия · нужна ") + file + L.T(" (найден ") + check.Installed + ")"; break;
                default: detail = null; break;
            }
            if (detail != null) Add(row, Text(detail, 12, Warn), 3);
            row.ToolTip = file;
            return row;
        }
        private void HelpPage(Panel p) {
            Hero(p, L.T("05   /   ИНСТРУКЦИЯ"), L.T("Как пользоваться"), L.T("Короткие шаги от подготовки профиля до проверки в игре."), "\uE897");
            var start = Stack(); Add(start, Text(L.T("Перед началом"), 18, Ink, true));
            Step(start, "1", L.T("Сделайте профиль локальным"), L.T("В меню выбора профиля игры откройте его настройки и снимите «Использовать Steam Cloud», если синхронизация включена."));
            Step(start, "2", L.T("Сохранитесь вручную"), L.T("Загрузите нужный профиль. В меню сохранения создайте ручное сохранение с понятным именем. Для переноса грузовиков сделайте это в обоих профилях."));
            Step(start, "3", L.T("Закройте игру и выберите ETS2 или ATS"), L.T("Если профиль не найден, нажмите «Папка игры…» и укажите папку с profiles. Кнопка ↻ перечитывает файлы."));
            Add(p, Box(start));
            var use = Stack(); Add(use, Text(L.T("Дальше выберите нужную вкладку"), 18, Ink, true));
            Step(use, "1", L.T("Профили"), L.T("Выберите исходный профиль, задайте новое имя и нажмите «Создать копию». Копия появится в меню игры."));
            Step(use, "2", L.T("Грузовики"), L.T("Выберите исходное и целевое сохранения, отметьте тягачи и укажите гараж. Если слотов не хватает, освободите место в игре, сохранитесь, закройте игру и нажмите ↻."));
            Step(use, "3", L.T("Деньги и опыт"), L.T("Выберите сохранение. В ETS2 можно отдельно добавить опыт грузовиков и автобусов Coaches. В ATS можно поднять оценки завершённых доставок Road Trip. Проверьте итог и загрузите это же сохранение в игре."));
            Step(use, "4", L.T("Сборки и карты"), L.T("Выберите сборку и профиль, затем примените порядок. Перед загрузкой профиля скачайте или обновите отмеченные моды. Ссылки и инструкции есть на странице сборки."));
            Step(use, "5", L.T("Управление"), L.T("Зайдите в настройки управления каждого профиля в игре и закройте игру. Выберите два профиля, проверьте предпросмотр и скопируйте настройки. Затем проверьте кнопки и оси в игре."));
            Add(p, Box(use), 14);
            var restore = Stack(); Add(restore, Text(L.T("Как отменить изменения"), 18, Ink, true));
            Step(restore, "1", L.T("Закройте игру и откройте резервную копию"), L.T("После записи кнопка появится внизу окна. Позже копию можно найти в папке .quper-backups рядом с изменёнными файлами."));
            Step(restore, "2", L.T("Верните исходные файлы"), L.T("Скопируйте исходные файлы из резервной копии в папку, где лежат изменённые файлы, и согласитесь на замену. Если есть created-files.txt, удалите из папки профиля файлы, перечисленные в нём. Затем откройте игру."));
            Add(p, Box(restore), 14);
            var feedback = Stack();
            Add(feedback, Text(L.T("Обратная связь"), 18, Ink, true));
            Add(feedback, Text(L.T("Расскажите, что понравилось, чего не хватает или что не получилось. Если нашли ошибку, укажите игру и шаг, на котором она возникла."), 13, Muted), 7);
            var chat = FooterLink(L.T("Открыть тему на форуме SCS  ↗"), FeedbackUrl);
            chat.HorizontalAlignment = HorizontalAlignment.Left;
            Add(feedback, chat, 9);
            Add(p, Box(feedback), 14);
        }
    }
}
