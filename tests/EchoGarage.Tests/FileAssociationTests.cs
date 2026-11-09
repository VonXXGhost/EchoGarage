using EchoGarage.Infrastructure.Windows;
using Microsoft.Win32;

namespace EchoGarage.Tests;

/// <summary>In-memory HKCU replacement for verifying every key path the app writes.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, object?>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, object?>> Keys => _keys;

    public bool SetValue(string keyPath, string? valueName, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        if (!_keys.TryGetValue(keyPath, out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _keys[keyPath] = values;
        }

        values[valueName ?? string.Empty] = value;
        return true;
    }

    public bool DeleteTree(string keyPath)
    {
        foreach (var key in _keys.Keys.Where(k =>
                     string.Equals(k, keyPath, StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(key);
        }

        return true;
    }

    public bool DeleteValue(string keyPath, string? valueName)
    {
        if (_keys.TryGetValue(keyPath, out var values))
        {
            values.Remove(valueName ?? string.Empty);
        }

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
        Assert.True(_service.Register(_exePath));
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
        Assert.True(_service.Register(_exePath));

        foreach (var extension in FileAssociationService.AudioExtensions)
        {
            Assert.False(_store.HasValue($@"Software\Classes\{extension}", string.Empty));
        }
    }

    [Fact]
    public void Register_SubsetAfterFullRegistration_RemovesUncheckedFormats()
    {
        Assert.True(_service.Register(_exePath));
        Assert.True(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));

        Assert.True(_service.Register(_exePath, [".mp3"]));

        Assert.True(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".flac"));
        Assert.True(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".mp3"));
    }

    [Fact]
    public void Register_OnlyWritesSelectedExtensions()
    {
        Assert.True(_service.Register(_exePath, [".mp3", "flac"]));

        Assert.True(_store.HasValue(@"Software\Classes\.mp3\OpenWithProgids", "EchoGarage.Test"));
        Assert.True(_store.HasValue(@"Software\Classes\.flac\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\.wav\OpenWithProgids", "EchoGarage.Test"));
        Assert.False(_store.HasValue(@"Software\Classes\Applications\EchoGarage.Test.exe\SupportedTypes", ".m4a"));
        Assert.True(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".mp3"));
        Assert.False(_store.HasValue(@"Software\EchoGarage.Test\Capabilities\FileAssociations", ".aac"));
    }

    [Fact]
    public void Register_WithoutExtensionsReturnFalse()
    {
        Assert.False(_service.Register(_exePath, Array.Empty<string>()));
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
    public void Register_ReturnsFalseForMissingExecutable()
    {
        Assert.False(_service.Register(_workspace.Combine("missing", "EchoGarage.exe")));
        Assert.False(_service.IsRegistered());
    }

    [Fact]
    public void Unregister_RemovesEveryCreatedKeyAndValue()
    {
        Assert.True(_service.Register(_exePath));
        Assert.True(_service.Unregister());

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
        _registered = _service.Register(_exePath);
        if (!_registered)
        {
            Assert.Skip("当前会话不允许写入 HKCU\\Software\\Classes，跳过真实注册表测试。");
            return;
        }

        Assert.True(_service.IsRegistered());
        using (var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{_progId}\shell\open\command"))
        {
            Assert.Contains(_exePath, (string?)command?.GetValue(null));
        }

        using (var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mp3\OpenWithProgids"))
        {
            Assert.NotNull(openWith?.GetValue(_progId));
        }

        Assert.True(_service.Unregister());
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
