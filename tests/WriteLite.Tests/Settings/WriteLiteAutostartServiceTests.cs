using WriteLite.Services.Settings;

namespace WriteLite.Tests.Settings;

[TestClass]
public sealed class WriteLiteAutostartServiceTests
{
    [TestMethod]
    public void CanEnable_RejectsDotnetExe()
    {
        var service = new WriteLiteAutostartService(
            exePathProvider: () => @"C:\Program Files\dotnet\dotnet.exe",
            registry: new FakeRegistry());
        Assert.IsFalse(service.CanEnableForCurrentBinary);
        service.SetEnabled(true);
        Assert.IsFalse(service.IsEnabled);
    }

    [TestMethod]
    public void SetEnabled_WritesQuotedPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "WriteLite-test.exe");
        File.WriteAllText(path, "stub");
        try
        {
            var reg = new FakeRegistry();
            var service = new WriteLiteAutostartService(() => path, reg);
            Assert.IsTrue(service.CanEnableForCurrentBinary);
            service.SetEnabled(true);
            Assert.IsTrue(service.IsEnabled);
            var value = reg.Values[WriteLiteAutostartService.RunValueName] as string;
            Assert.IsNotNull(value);
            Assert.IsTrue(value.StartsWith('\"'));
            Assert.IsTrue(value.Contains(path, StringComparison.OrdinalIgnoreCase));
            service.SetEnabled(false);
            Assert.IsFalse(service.IsEnabled);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SetEnabled_False_NoThrowWhenMissing()
    {
        var reg = new FakeRegistry();
        var service = new WriteLiteAutostartService(
            () => Path.Combine(Path.GetTempPath(), "WriteLite.exe"),
            reg);
        service.SetEnabled(false);
        Assert.IsFalse(service.IsEnabled);
    }

    [TestMethod]
    public void SetEnabled_Twice_DoesNotCreateDuplicateKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "WriteLite-nodupe.exe");
        File.WriteAllText(path, "stub");
        try
        {
            var reg = new FakeRegistry();
            var service = new WriteLiteAutostartService(() => path, reg);
            service.SetEnabled(true);
            service.SetEnabled(true);
            Assert.HasCount(1, reg.Values);
            Assert.IsTrue(reg.Values.ContainsKey(WriteLiteAutostartService.RunValueName));
            service.SetEnabled(false);
            Assert.IsEmpty(reg.Values);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private sealed class FakeRegistry : IRegistryRoot
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IRegistryKey? OpenSubKey(string name, bool writable)
            => new FakeKey(Values);
    }

    private sealed class FakeKey(Dictionary<string, object> values) : IRegistryKey
    {
        public void SetValue(string name, object value) => values[name] = value;

        public void DeleteValue(string name, bool throwOnMissing)
        {
            if (!values.Remove(name) && throwOnMissing)
            {
                throw new ArgumentException("missing");
            }
        }

        public object? GetValue(string name)
            => values.TryGetValue(name, out var v) ? v : null;

        public void Dispose()
        {
        }
    }
}
