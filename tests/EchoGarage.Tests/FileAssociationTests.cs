using EchoGarage.Infrastructure.Windows;
using Microsoft.Win32;

namespace EchoGarage.Tests;

/// <summary>In-memory HKCU replacement for verifying every key path the app writes.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, object?>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, object?>> Keys => _keys;

    /// <summary>返回 true 表示该次写入被“只读注册表项”拒绝（模拟安全软件保护的扩展名键）。</summary>
    public Func<string, string?, bool>? WriteDenied { get; set; }

    /// <summary>返回 true 表示该次删除被拒绝。</summary>
    public Func<string, string?, bool>? DeleteDenied { get; set; }

    public string? LastError { get; private set; }

    public bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        if (WriteDenied?.Invoke(keyPath, valueName) == true)
        {
            LastError = $"模拟：HKCU\\{keyPath} 是只读的。";
            return false;
        }

        if (!_keys.TryGetValue(keyPath, out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _keys[keyPath] = values;
        }

        values[valueName ?? string.Empty] = value;
        LastError = null;
        return true;
    }

    public bool DeleteTree(string keyPath)
    {
        if (DeleteDenied?.Invoke(keyPath, null) == true)
        {
            LastError = $"模拟：无法删除 HKCU\\{keyPath}。";
            return false;
        }

        foreach (var key in _keys.Keys.Where(k =>
                     string.Equals(k, keyPath, StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(key);
        }

        LastError = null;
        return true;
    }

    public bool DeleteValue(string keyPath, string? valueName)
    {
        if (DeleteDenied?.Invoke(keyPath, valueName) == true)
        {
            LastError = $"模拟：无法删除 HKCU\\{keyPath} 下的值。";
            return false;
        }

        if (_keys.TryGetValue(keyPath, out var values))
        {
            values.Remove(valueName ?? string.Empty);
        }

        LastError = null;
        return true;
    }

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public object? GetValue(string keyPath, string? valueName) =>
        _keys.TryGetValue(keyPath, out var values) &&
        values.TryGetValue(valueName ?? string.Empty, out var value)
            ? value
            : null;

    public bool HasValue(string keyPath, string? valueName) =>
        _keys.TryGetValue(keyPath, out var values) && values.ContainsKey(valueName ?? string.Empty);

    public void DenyWritesUnder(string keyPathPrefix) =>
        WriteDenied = (path, _) => path.StartsWith(keyPathPrefix, StringComparison.OrdinalIgnoreCase);

    public void DenyWrite(string keyPath, string? valueName) =>
        WriteDenied = (path, name) =>
            string.Equals(path, keyPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase);
}

public sealed class FileAssociationLogicTests
{
    private readonly string _exePath;
    private readonly FakeRegistryStore _store = new();
    private readonly FileAssociationService _service;
    private readonly TempWorkspace _workspace = new();

    public FileAssociationLogicTests()
    {
        _exePath = _workspace.Combine("app", "EchoGarage.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_exePath)!);
        File.WriteAllText(_exePath, string.Empty);
        _service = new FileAssociationService("EchoGarage.Test", "EchoGarage.Test.exe", "EchoGarage Test", _store);
    }

    [Fact]
    public void Register_WritesExpectedKeysAndValues()
    {
        var result = _service.Register(_exePath);
        Assert.True(result.IsSuccess);
        Assert.True(_service.IsRegistered());

        Assert.Equal("EchoGarage Test 音乐文件", _store.GetValue(@"Software\Classes\EchoGarage.Test", null));
        Assert.Equal($"\"{_exePath}\",0", _store.GetValue(@"Software\Classes\EchoGarage.Test\DefaultIcon", null));
        Assert.Equal($"\"{_exePath}\" \"%1\"",
            _store.GetValue(@"Software\Classes\EchoGarage.Test\shell\open\command", null));

        Assert.Equal("EchoGarage Test",
            _store.GetValue(@"Software\Classes\Applications\EchoGarage.Test.exe", "FriendlyAppName"));

        foreach (var extension in FileAssociationService.AudioExtensions)
        {
            Assert.True(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", extension));
            Assert.True(_store.HasValue($@"Software\Classes\{extension}\OpenWithProgids", "EchoGarage.Test"));
            Assert.Equal("EchoGarage.Test",
                _store.GetValue($@"Software\EchoGarage.Test\Capabilities\FileAssociations", extension));
        }

        Assert.Equal(@"Software\EchoGarage.Test\Capabilities",
            _store.GetValue(@"Software\RegisteredApplications", "EchoGarage Test"));
    }

    [Fact]
    public void Register_DoesNotTakeOverExtensionDefaultHandlers()
    {
        Assert.True(_service.Register(_exePath).IsSuccess);

        foreach (var extension in FileAssociationService.AudioExtensions)
        {
            Assert.False(_store.HasValue($@"Software\Classes\{extension}", string.Empty));
        }
    }

    [Fact]
    public void Register_SubsetAfterFullRegistration_RemovesUncheckedFormats()
    {
        Assert.True(_service.Register(_exePath).IsSuccess);
        Assert.True(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));

        Assert.True(_service.Register(_exePath, [".mp3"]).IsSuccess);

        Assert.True(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".flac"));
        Assert.False(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", ".flac"));
        Assert.True(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".mp3"));
    }

    [Fact]
    public void Register_OnlyWritesSelectedExtensions()
    {
        Assert.True(_service.Register(_exePath, [".mp3", "flac"]).IsSuccess);

        Assert.True(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.True(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\.wav\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", ".m4a"));
        Assert.True(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".mp3"));
        Assert.False(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".aac"));
    }

    [Fact]
    public void Register_WithoutExtensionsReportsEmptySelection()
    {
        var result = _service.Register(_exePath, Array.Empty<string>());

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.SelectedCount);
        Assert.False(_service.IsRegistered());
    }

    [Fact]
    public void NormalizeExtensions_AddsDotAndFiltersUnknownValues()
    {
        var normalized = FileAssociationService.NormalizeExtensions(["mp3", ".FLAC", ".txt", " "]);

        Assert.Equal([".mp3", ".flac"], normalized);
        Assert.Equal(FileAssociationService.AudioExtensions, FileAssociationService.NormalizeExtensions(null));
    }

    [Fact]
    public void Register_ReportsExecutableUnavailableForMissingExecutable()
    {
        var result = _service.Register(_workspace.Combine("missing", "EchoGarage.exe"));

        Assert.False(result.ExecutableAvailable);
        Assert.False(result.IsSuccess);
        Assert.False(_service.IsRegistered());
    }

    [Fact]
    public void Register_ReadOnlyOpenWithProgids_IsPartialButStillSucceeds()
    {
        // 复现用户机器：HKCU\Software\Classes\.flac 被系统保护为只读。
        _store.DenyWritesUnder(@"Software\Classes\.flac");

        var result = _service.Register(_exePath);

        Assert.True(result.IsSuccess);
        Assert.True(result.CoreRegistered);
        Assert.Equal([".flac"], result.PartialExtensions);
        Assert.Empty(result.FailedExtensions);

        // Capabilities 与 SupportedTypes 仍然写入，Windows“默认应用”能看到 EchoGarage。
        Assert.Equal("EchoGarage.Test",
            _store.GetValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".flac"));
        Assert.True(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", ".flac"));
        Assert.False(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
        Assert.Contains(".mp3", result.RegisteredExtensions);
    }

    [Fact]
    public void Register_OneFormatCapabilitiesDenied_ContinuesWithOtherFormats()
    {
        _store.DenyWrite(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".wav");

        var result = _service.Register(_exePath);

        Assert.False(result.IsSuccess);
        Assert.Equal([".wav"], result.FailedExtensions);
        Assert.Equal(FileAssociationService.AudioExtensions.Count - 1, result.RegisteredExtensions.Count);
        Assert.True(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.True(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", ".wav"));
    }

    [Fact]
    public void Register_CoreEntryDenied_ReportsCoreFailureButKeepsFormatEntries()
    {
        _store.DenyWrite(@"Software\RegisteredApplications", "EchoGarage Test");

        var result = _service.Register(_exePath);

        Assert.False(result.CoreRegistered);
        Assert.False(result.IsSuccess);
        Assert.Equal(FileAssociationService.AudioExtensions.Count, result.RegisteredExtensions.Count);
        Assert.False(_store.HasValue(@"Software\RegisteredApplications", "EchoGarage Test"));
    }

    [Fact]
    public void GetRegistrationState_ReflectsCapabilitiesAndOpenWithEntries()
    {
        Assert.True(_service.Register(_exePath, [".mp3"]).IsSuccess);

        var state = _service.GetRegistrationState();

        Assert.Contains(state, s => s.Extension == ".mp3" && s.Registered && s.OpenWithRegistered);
        Assert.Contains(state, s => s.Extension == ".wav" && !s.Registered && !s.OpenWithRegistered);
        Assert.Equal(FileAssociationService.AudioExtensions.Count, state.Count);
    }

    [Fact]
    public void Unregister_RemovesEveryCreatedKeyAndValue()
    {
        Assert.True(_service.Register(_exePath).IsSuccess);
        var cleanup = _service.Unregister();

        Assert.True(cleanup.IsSuccess);
        Assert.False(_service.IsRegistered());
        Assert.False(_store.KeyExists(@"Software\Classes\EchoGarage.Test"));
        Assert.False(_store.KeyExists(@"Software\Classes\Applications\EchoGarage.Test.exe"));
        Assert.False(_store.KeyExists(@"Software\EchoGarage.Test"));

        foreach (var extension in FileAssociationService.AudioExtensions)
        {
            Assert.False(_store.HasValue($@"Software\Classes\{extension}\OpenWithProgids", "EchoGarage.Test"));
        }

        Assert.False(_store.HasValue(@"Software\RegisteredApplications", "EchoGarage Test"));
    }

    [Fact]
    public void Unregister_WithoutRegistration_IsSuccess()
    {
        // 只读扩展名键本来就没有我们的值，不应因为“删不掉”而报失败。
        var cleanup = _service.Unregister();

        Assert.True(cleanup.CoreCleared);
        Assert.Empty(cleanup.LockedExtensions);
        Assert.True(cleanup.IsSuccess);
    }

    [Fact]
    public void Unregister_LockedOpenWithValue_IsReportedButCoreIsCleared()
    {
        Assert.True(_service.Register(_exePath).IsSuccess);
        _store.DeleteDenied = (path, name) =>
            string.Equals(path, @"Software\Classes\.flac\OpenWithProgids", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(name, "EchoGarage.Test", StringComparison.OrdinalIgnoreCase);

        var cleanup = _service.Unregister();

        Assert.True(cleanup.CoreCleared);
        Assert.Equal([".flac"], cleanup.LockedExtensions);
        Assert.False(cleanup.IsSuccess);
        Assert.False(_service.IsRegistered());
        Assert.False(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.True(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
    }
}

public sealed class FileAssociationRegistryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly string _progId = "EchoGarage.Test." + Guid.NewGuid().ToString("N");
    private readonly FileAssociationService _service;
    private readonly string _exePath;
    private bool _registered;

    public FileAssociationRegistryTests()
    {
        _service = new FileAssociationService(_progId, _progId + ".exe", _progId);
        _exePath = _workspace.Combine("app", "EchoGarageTest.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_exePath)!);
        File.WriteAllText(_exePath, string.Empty);
    }

    [Fact]
    public void RegisterAndUnregister_AgainstRealRegistry()
    {
        // 用 .wav 而不是 .mp3：部分机器的 .mp3 扩展名键是只读的（OpenWithProgids 会写入失败）。
        var result = _service.Register(_exePath, [".wav"]);
        _registered = result.CoreRegistered;
        if (!result.CoreRegistered || result.FailedExtensions.Count > 0)
        {
            Assert.Skip("当前会话不允许写入 HKCU\\Software\\Classes，跳过真实注册表测试。");
            return;
        }

        Assert.True(_service.IsRegistered());
        using (var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{_progId}\shell\open\command"))
        {
            Assert.Contains(_exePath, (string?)command?.GetValue(null));
        }

        Assert.Equal(_progId,
            Registry.CurrentUser.OpenSubKey($@"Software\{_progId}\Capabilities\FileAssociations")?.GetValue(".wav"));

        if (result.PartialExtensions.Count == 0)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.wav\OpenWithProgids");
            Assert.NotNull(openWith?.GetValue(_progId));
        }

        Assert.True(_service.Unregister().CoreCleared);
        _registered = false;
        Assert.False(_service.IsRegistered());
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\{_progId}"));
    }

    public void Dispose()
    {
        if (_registered)
        {
            _service.Unregister();
        }

        _workspace.Dispose();
    }
}
