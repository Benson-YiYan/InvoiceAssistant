using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Http;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InvoiceAssistant.WinUI;

public sealed partial class MainWindow : Window
{
    private static readonly string[] CategoryNames =
    [
        "差旅交通", "餐饮招待", "住宿", "办公用品", "通讯网络", "软件服务",
        "物流快递", "租赁物业", "水电能源", "咨询服务", "法律服务", "医疗健康", "其他"
    ];

    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InvoiceAssistant");
    private readonly string _legacyDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "invoice-sorter");
    private readonly ObservableCollection<InvoiceRecord> _allInvoices = [];
    private readonly ObservableCollection<FolderRecord> _folders = [];
    private string _currentFolderId = "root";
    private bool _isReloading;
    private AppSettings _settings = new();
    private const string CurrentVersion = "0.2.0";
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/Benson-YiYan/InvoiceAssistant/main/windows-update.json";

    public MainWindow()
    {
        InitializeComponent();
        LoadData();
        PopulateFolders();
        RefreshInvoices();
        ExtendsContentIntoTitleBar = false;
        Title = "发票小秘书";
    }

    private FrameworkElement RootXamlElement => (FrameworkElement)Content;

    private void LoadData()
    {
        Directory.CreateDirectory(_dataDirectory);
        LoadJson(Path.Combine(_dataDirectory, "records.json"), _allInvoices);
        LoadJson(Path.Combine(_dataDirectory, "folders.json"), _folders);
        try
        {
            var settingsPath = Path.Combine(_dataDirectory, "settings.json");
            if (File.Exists(settingsPath)) _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), JsonOptions) ?? new();
        }
        catch { }
        if (_allInvoices.Count == 0 && _folders.Count == 0)
        {
            LoadJson(Path.Combine(_legacyDataDirectory, "records.json"), _allInvoices);
            LoadJson(Path.Combine(_legacyDataDirectory, "folders.json"), _folders);
            if (_allInvoices.Count > 0 || _folders.Count > 0)
                StatusText.Text = "已读取旧版 Windows 记录；旧文件保持不变。";
        }

        for (var i = 0; i < CategoryNames.Length; i++)
        {
            var name = CategoryNames[i];
            if (!_folders.Any(folder => folder.Category == name))
                _folders.Add(new FolderRecord { Id = $"category-{i + 1}", Name = name, ParentId = "root", Category = name });
        }
        foreach (var invoice in _allInvoices)
            invoice.PropertyChanged += Invoice_PropertyChanged;
        SaveData();
    }

    private static void LoadJson<T>(string path, ICollection<T> target)
    {
        try
        {
            var json = File.ReadAllText(path);
            foreach (var item in JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [])
                target.Add(item);
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private void SaveData()
    {
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(Path.Combine(_dataDirectory, "records.json"),
            JsonSerializer.Serialize(_allInvoices, JsonOptions));
        File.WriteAllText(Path.Combine(_dataDirectory, "folders.json"),
            JsonSerializer.Serialize(_folders, JsonOptions));
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), JsonSerializer.Serialize(_settings, JsonOptions));
    }

    private void PopulateFolders()
    {
        _isReloading = true;
        FolderTree.RootNodes.Clear();
        var root = new TreeViewNode { Content = new FolderRecord { Id = "root", Name = "全部发票" }, IsExpanded = true };
        AddFolderNodes(root, "root");
        FolderTree.RootNodes.Add(root);
        FolderTree.SelectedNode = FindNode(root, _currentFolderId) ?? root;
        FolderHeading.Text = _currentFolderId == "root"
            ? "全部发票"
            : _folders.FirstOrDefault(folder => folder.Id == _currentFolderId)?.Name ?? "全部发票";
        _isReloading = false;
    }

    private void AddFolderNodes(TreeViewNode parent, string parentId)
    {
        foreach (var folder in _folders.Where(item => item.ParentId == parentId).OrderBy(item => item.Name))
        {
            var node = new TreeViewNode { Content = folder, IsExpanded = true };
            parent.Children.Add(node);
            AddFolderNodes(node, folder.Id);
        }
    }

    private static TreeViewNode? FindNode(TreeViewNode node, string folderId)
    {
        if (node.Content is FolderRecord folder && folder.Id == folderId) return node;
        foreach (var child in node.Children)
            if (FindNode(child, folderId) is { } found) return found;
        return null;
    }

    private HashSet<string> DescendantIds(string id)
    {
        var result = new HashSet<string> { id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in _folders)
                if (result.Contains(folder.ParentId) && result.Add(folder.Id)) changed = true;
        }
        return result;
    }

    private IEnumerable<InvoiceRecord> VisibleInvoices()
    {
        var folderIds = DescendantIds(_currentFolderId);
        return _allInvoices.Where(invoice => folderIds.Contains(invoice.FolderId));
    }

    private void RefreshInvoices()
    {
        var rows = VisibleInvoices().ToList();
        InvoiceList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary(rows);
    }

    private void UpdateSummary(IReadOnlyCollection<InvoiceRecord>? visible = null)
    {
        var rows = visible ?? VisibleInvoices().ToList();
        var chosen = InvoiceList.SelectedItems.Cast<InvoiceRecord>().ToList();
        var totals = chosen.Count > 0 ? chosen : rows;
        AmountSummary.Text = totals.Sum(invoice => invoice.Amount ?? 0).ToString("C2", CultureInfo.GetCultureInfo("zh-CN"));
        CountSummary.Text = $"{totals.Count} 张发票";
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        picker.FileTypeFilter.Add(".doc");
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        ImportButton.IsEnabled = false;
        var imported = 0;
        foreach (var file in files)
        {
            try
            {
                var text = await ExtractTextAsync(file);
                var amount = ParseAmount(text);
                var record = new InvoiceRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = file.Name,
                    FilePath = file.Path,
                    FileType = Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant(),
                    Text = text,
                    Amount = amount,
                    Category = GuessCategory(text),
                    Status = string.IsNullOrWhiteSpace(text) ? "读取失败" : "待复核",
                    FolderId = _currentFolderId
                };
                record.PropertyChanged += Invoice_PropertyChanged;
                _allInvoices.Add(record);
                imported++;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"导入 {file.Name} 失败：{ex.Message}";
            }
        }
        SaveData();
        RefreshInvoices();
        PopulateFolders();
        ImportButton.IsEnabled = true;
        StatusText.Text = $"已导入 {imported} 张；原始文件保留在原位置。";
    }

    private static async Task<string> ExtractTextAsync(StorageFile file)
    {
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (extension == ".docx")
        {
            using var stream = await file.OpenStreamForReadAsync();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is null) return "";
            using var xml = entry.Open();
            var document = System.Xml.Linq.XDocument.Load(xml);
            XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join(Environment.NewLine, document.Descendants(word + "p")
                .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(text => text.Value))));
        }
        if (extension == ".doc")
            throw new NotSupportedException("Windows 版读取 .doc 需要本机安装 Microsoft Word；请另存为 .docx 后导入。");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR 语言组件不可用，请在 Windows 设置中安装中文或英文 OCR 语言包。");
        if (extension == ".pdf")
        {
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            var pages = new List<string>();
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                using var page = pdf.GetPage(i);
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync();
                pages.Add((await engine.RecognizeAsync(bitmap)).Text);
            }
            return string.Join(Environment.NewLine, pages);
        }

        using var imageStream = await file.OpenAsync(FileAccessMode.Read);
        var imageDecoder = await BitmapDecoder.CreateAsync(imageStream);
        using var image = await imageDecoder.GetSoftwareBitmapAsync();
        return (await engine.RecognizeAsync(image)).Text;
    }

    private static decimal? ParseAmount(string text)
    {
        var match = Regex.Match(text, @"(?:价税合计|合计金额|总金额|金额)[：:\s]*[¥￥]?\s*([0-9,]+(?:\.[0-9]{1,2})?)");
        return match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture, out var amount)
            ? amount : null;
    }

    private static string GuessCategory(string text)
    {
        if (Regex.IsMatch(text, "酒店|宾馆|住宿")) return "住宿";
        if (Regex.IsMatch(text, "餐饮|餐厅|饭店")) return "餐饮招待";
        if (Regex.IsMatch(text, "机票|火车|交通|加油")) return "差旅交通";
        return "其他";
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_isReloading) return;
        var folder = args.InvokedItem switch
        {
            FolderRecord record => record,
            TreeViewNode node => node.Content as FolderRecord,
            _ => null
        };
        if (folder is null) return;
        _currentFolderId = folder.Id;
        PopulateFolders();
        RefreshInvoices();
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "文件夹名称" };
        var dialog = new ContentDialog
        {
            Title = "新建文件夹",
            Content = input,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            XamlRoot = RootXamlElement.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        _folders.Add(new FolderRecord { Id = $"folder-{Guid.NewGuid()}", Name = input.Text.Trim(), ParentId = _currentFolderId });
        SaveData();
        PopulateFolders();
    }

    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = _folders.FirstOrDefault(item => item.Id == _currentFolderId);
        if (folder is null || folder.Category is not null) return;
        var dialog = new ContentDialog
        {
            Title = $"删除“{folder.Name}”及其下级内容？",
            Content = "此操作会删除应用中的归档记录，但不会删除磁盘上的原始发票文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootXamlElement.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var ids = DescendantIds(folder.Id);
        foreach (var child in _folders.Where(item => ids.Contains(item.Id)).ToList()) _folders.Remove(child);
        foreach (var invoice in _allInvoices.Where(item => ids.Contains(item.FolderId)).ToList()) _allInvoices.Remove(invoice);
        _currentFolderId = folder.ParentId;
        SaveData();
        PopulateFolders();
        RefreshInvoices();
    }

    private void InvoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = InvoiceList.SelectedItems.Cast<InvoiceRecord>().ToList();
        if (selected.Count == 0) return;
        var dialog = new ContentDialog
        {
            Title = $"删除所选的 {selected.Count} 张发票记录？",
            Content = "只删除应用中的归档记录，不会删除磁盘上的原始文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = RootXamlElement.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var invoice in selected) _allInvoices.Remove(invoice);
        SaveData();
        RefreshInvoices();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
        picker.SuggestedFileName = "发票归档.csv";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var rows = InvoiceList.SelectedItems.Count > 0
            ? InvoiceList.SelectedItems.Cast<InvoiceRecord>()
            : VisibleInvoices();
        var csv = new StringBuilder("文件名,销方,日期,发票号,费用类别,金额,状态,文件路径\r\n");
        foreach (var row in rows)
            csv.AppendLine(string.Join(",", new[] { row.FileName, row.Vendor, row.Date, row.InvoiceNo, row.Category,
                row.Amount?.ToString(CultureInfo.InvariantCulture) ?? "", row.Status, row.FilePath }.Select(CsvEscape)));
        await FileIO.WriteTextAsync(file, csv.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
        StatusText.Text = $"已导出 {rows.Count()} 张发票。";
    }

    private static string CsvEscape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private async void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        var candidates = (InvoiceList.SelectedItems.Count > 0
            ? InvoiceList.SelectedItems.Cast<InvoiceRecord>()
            : VisibleInvoices()).Where(item => !string.IsNullOrWhiteSpace(item.Text)).ToList();
        if (candidates.Count == 0) { StatusText.Text = "当前范围没有可供 AI 复核的已识别文字。"; return; }
        var checks = new StackPanel { Spacing = 6 };
        var boxes = candidates.Select(item => new CheckBox
        {
            Content = $"{item.FileName}  ·  {item.Category}  ·  {(item.Amount?.ToString("C2", CultureInfo.GetCultureInfo("zh-CN")) ?? "金额待识别")}",
            IsChecked = true
        }).ToList();
        foreach (var box in boxes) checks.Children.Add(box);
        var scroll = new ScrollViewer { Content = checks, MaxHeight = 360 };
        var dialog = new ContentDialog
        {
            Title = $"即将使用 AI 复核以下的 {candidates.Count} 张发票，确定吗？",
            Content = scroll,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = RootXamlElement.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var chosen = candidates.Where((_, index) => boxes[index].IsChecked == true).ToList();
        if (chosen.Count == 0) return;
        ReviewButton.IsEnabled = false;
        foreach (var invoice in chosen)
        {
            try { await ReviewInvoiceAsync(invoice); }
            catch (Exception ex) { invoice.Status = "复核失败"; StatusText.Text = ex.Message; }
        }
        SaveData();
        RefreshInvoices();
        ReviewButton.IsEnabled = true;
        StatusText.Text = $"已完成 {chosen.Count} 张发票的 AI 复核。";
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = new ComboBox { ItemsSource = new[] { "API（兼容 OpenAI 格式）", "已安装的 Codex CLI", "DeepSeek Harness" }, SelectedIndex = _settings.Provider switch { "api" => 0, "harness" => 2, _ => 1 } };
        var endpoint = new TextBox { Text = _settings.Endpoint, PlaceholderText = "https://api.example.com/v1" };
        var model = new TextBox { Text = _settings.Model, PlaceholderText = "模型名称" };
        var key = new PasswordBox { PlaceholderText = HasApiKey() ? "已保存密钥；输入新值可替换" : "输入 API 密钥" };
        var codex = new TextBox { Text = _settings.CodexCommand, PlaceholderText = "留空自动查找 codex" };
        var harness = new TextBox { Text = _settings.HarnessCommand, PlaceholderText = "命令接收 stdin 并返回 JSON" };
        var checkUpdate = new Button { Content = "检查更新", HorizontalAlignment = HorizontalAlignment.Left };
        var updateText = new TextBlock { Text = "更新状态：尚未检查", TextWrapping = TextWrapping.Wrap };
        checkUpdate.Click += async (_, _) => await CheckForUpdatesAsync(updateText);
        var clearKey = new Button { Content = "清除已保存密钥", HorizontalAlignment = HorizontalAlignment.Left };
        clearKey.Click += (_, _) => { _settings.ApiKey = ""; key.Password = ""; SaveData(); updateText.Text = "已清除 API 密钥。"; };
        var form = new StackPanel { Spacing = 12, MinWidth = 560 };
        foreach (var pair in new (string, Control)[] { ("整理方式", provider), ("API 地址", endpoint), ("模型名称", model), ("API 密钥", key), ("Codex 命令", codex), ("Harness 命令", harness) })
        { form.Children.Add(new TextBlock { Text = pair.Item1, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); form.Children.Add(pair.Item2); }
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { clearKey, checkUpdate } });
        form.Children.Add(updateText);
        var dialog = new ContentDialog { Title = "AI 设置与检查更新", Content = new ScrollViewer { Content = form, MaxHeight = 560 }, PrimaryButtonText = "保存设置", CloseButtonText = "取消", XamlRoot = RootXamlElement.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _settings.Provider = provider.SelectedIndex switch { 0 => "api", 2 => "harness", _ => "codex" };
        _settings.Endpoint = endpoint.Text.Trim(); _settings.Model = model.Text.Trim();
        if (!string.IsNullOrWhiteSpace(key.Password)) _settings.ApiKey = Protect(key.Password);
        _settings.CodexCommand = codex.Text.Trim(); _settings.HarnessCommand = harness.Text.Trim();
        SaveData();
        StatusText.Text = "AI 设置已保存到本机。";
    }

    private bool HasApiKey() => !string.IsNullOrWhiteSpace(_settings.ApiKey);
    private static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string value)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    private async Task ReviewInvoiceAsync(InvoiceRecord invoice)
    {
        var prompt = $"请严格只返回 JSON：{{\"vendor\":\"\",\"date\":\"YYYY-MM-DD\",\"invoiceNo\":\"\",\"amount\":0,\"category\":\"费用类别\",\"summary\":\"\",\"needsReview\":false}}。费用类别只能从 {string.Join("、", CategoryNames)} 中选择。发票文件名：{invoice.FileName}\\n识别文字：\\n{invoice.Text}";
        string raw;
        if (_settings.Provider == "api")
        {
            var endpoint = _settings.Endpoint.TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions")) endpoint += "/chat/completions";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var apiKey = Unprotect(_settings.ApiKey);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new { model = _settings.Model, temperature = 0, response_format = new { type = "json_object" }, messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API 返回 {((int)response.StatusCode)}：{body[..Math.Min(240, body.Length)]}");
            using var doc = JsonDocument.Parse(body); raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        else
        {
            var command = _settings.Provider == "harness" ? _settings.HarnessCommand : (_settings.CodexCommand.Length > 0 ? _settings.CodexCommand : "codex");
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("请先配置 AI 命令。");
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 AI 命令。");
            await process.StandardInput.WriteAsync(prompt); process.StandardInput.Close();
            raw = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("AI 命令执行失败。");
        }
        var jsonStart = raw.IndexOf('{'); var jsonEnd = raw.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart) throw new InvalidOperationException("AI 返回不是有效 JSON。");
        using var result = JsonDocument.Parse(raw[jsonStart..(jsonEnd + 1)]);
        var root = result.RootElement;
        if (root.TryGetProperty("vendor", out var vendor)) invoice.Vendor = vendor.GetString() ?? invoice.Vendor;
        if (root.TryGetProperty("date", out var date)) invoice.Date = date.GetString() ?? invoice.Date;
        if (root.TryGetProperty("invoiceNo", out var number)) invoice.InvoiceNo = number.GetString() ?? invoice.InvoiceNo;
        if (root.TryGetProperty("summary", out var summary)) invoice.Summary = summary.GetString() ?? "";
        if (root.TryGetProperty("amount", out var amount) && amount.TryGetDecimal(out var value)) invoice.Amount = value;
        if (root.TryGetProperty("category", out var category) && CategoryNames.Contains(category.GetString())) invoice.Category = category.GetString()!;
        invoice.Status = "已复核";
    }

    private async Task CheckForUpdatesAsync(TextBlock status)
    {
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync(UpdateManifestUrl);
            var manifest = JsonSerializer.Deserialize<WindowsUpdateManifest>(json, JsonOptions) ?? throw new InvalidOperationException("更新清单无效。");
            if (string.Compare(manifest.Version, CurrentVersion, StringComparison.OrdinalIgnoreCase) <= 0) { status.Text = $"更新状态：当前已是最新版本（{CurrentVersion}）。"; return; }
            status.Text = $"发现新版本 {manifest.Version}，正在下载并校验…";
            var bytes = await client.GetByteArrayAsync(manifest.DownloadUrl);
            if (bytes.Length != manifest.Size) throw new InvalidOperationException("下载大小校验失败。");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SHA-256 校验失败。");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", $"发票小秘书-Setup-{manifest.Version}-x64.exe");
            await File.WriteAllBytesAsync(path, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            status.Text = "下载并校验完成，安装程序已打开。";
        }
        catch (Exception ex) { status.Text = $"更新检查失败：{ex.Message}"; }
    }

    private void Invoice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveData();
        UpdateSummary();
    }
}

public sealed class FolderRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParentId { get; set; } = "root";
    public string? Category { get; set; }
    public override string ToString() => Name;
}

public sealed class InvoiceRecord : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileType { get; set; } = "";
    public string Text { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Date { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    public string Status { get; set; } = "待复核";
    public string FolderId { get; set; } = "root";
    public string Summary { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    private decimal? _amount;
    public decimal? Amount
    {
        get => _amount;
        set { if (_amount == value) return; _amount = value; OnPropertyChanged(); OnPropertyChanged(nameof(AmountValue)); }
    }
    public double AmountValue
    {
        get => (double)(Amount ?? 0m);
        set => Amount = double.IsFinite(value) ? (decimal)value : null;
    }
    private string _category = "其他";
    public string Category
    {
        get => _category;
        set { if (_category == value) return; _category = value; OnPropertyChanged(); }
    }
    public IReadOnlyList<string> Categories { get; } = new[]
    {
        "差旅交通", "餐饮招待", "住宿", "办公用品", "通讯网络", "软件服务",
        "物流快递", "租赁物业", "水电能源", "咨询服务", "法律服务", "医疗健康", "其他"
    };
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppSettings
{
    public string Provider { get; set; } = "codex";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string CodexCommand { get; set; } = "";
    public string HarnessCommand { get; set; } = "";
}

public sealed class WindowsUpdateManifest
{
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "x64";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}
