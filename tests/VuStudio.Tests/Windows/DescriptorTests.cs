using System.Text.Json;
using System.Text.Json.Nodes;
using VuStudio.Core.Config;
using VuStudio.Core.Providers;
using VuStudio.Sources.Windows.HwInfo;
using VuStudio.Sources.Windows.Lhm;

namespace VuStudio.Tests.Windows;

public class WindowsDescriptorTests
{
    public static TheoryData<string> Types => ["lhm", "hwinfo"];

    private static IProviderFactory Factory(string type) => type == "lhm" ? new LhmProviderFactory() : new HwInfoProviderFactory();

    [Fact]
    public void Factories_are_discovered_by_reflection_and_constructible_here()
    {
        var found = ProviderCatalog.Discover(typeof(LhmProviderFactory).Assembly);

        Assert.Equal(["hwinfo", "lhm"], found.Select(f => f.Descriptor.Type).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Descriptor_follows_the_conventions(string type)
    {
        var d = Factory(type).Descriptor;

        Assert.Equal(type, d.Type);
        Assert.Equal(PlatformSupport.Windows, d.Platforms);
        Assert.Equal(ProviderGroup.ThisPc, d.Group);
        Assert.True(d.Singleton);
        Assert.True(d.AutoCreate);
        Assert.False(d.RunsInUserSession);
        Assert.True(IconSet.Contains(d.Icon), d.Icon);
        Assert.Equal(OperatingSystem.IsWindows(), d.IsSupportedHere);
        Assert.Matches("^[a-z0-9][a-z0-9-]{0,62}$", d.Type);
        AssertBilingual(d.Name, nameof(d.Name), requireTranslation: false);
        AssertBilingual(d.Description!, nameof(d.Description));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Every_setting_has_english_and_italian_text_and_a_valid_default(string type)
    {
        var fields = Factory(type).Descriptor.Settings;

        Assert.NotEmpty(fields);
        Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var field in fields)
        {
            AssertBilingual(field.Label, field.Key);
            if (field.Help is not null)
            {
                AssertBilingual(field.Help, field.Key + " help");
            }

            Assert.NotNull(field.Default);
            switch (field.Kind)
            {
                case SettingKind.Boolean:
                    Assert.True(field.Default!.GetValueKind() is JsonValueKind.True or JsonValueKind.False, field.Key);
                    break;
                case SettingKind.Seconds:
                    var seconds = field.Default!.GetValue<double>();
                    Assert.InRange(seconds, field.Min!.Value, field.Max!.Value);
                    break;
                case SettingKind.TextList:
                    Assert.IsType<JsonArray>(field.Default);
                    break;
                default:
                    Assert.Fail($"Unexpected kind {field.Kind} for {field.Key}");
                    break;
            }
        }
    }

    [Fact]
    public void Lhm_settings_match_the_spec()
    {
        var fields = new LhmProviderFactory().Descriptor.Settings.ToDictionary(f => f.Key);

        Assert.Equal(SettingKind.Seconds, fields["interval"].Kind);
        Assert.Equal(1.0, fields["interval"].Default!.GetValue<double>());
        foreach (var key in new[] { "cpu", "gpu", "memory", "motherboard", "controllers", "storage", "network", "psu", "battery" })
        {
            Assert.Equal(SettingKind.Boolean, fields[key].Kind);
            Assert.Equal(key != "controllers", fields[key].Default!.GetValue<bool>());
        }

        Assert.Equal(SettingKind.TextList, fields["sensorTypes"].Kind);
        Assert.Equal(11, fields.Count);
    }

    [Fact]
    public void Hwinfo_settings_match_the_spec()
    {
        var fields = new HwInfoProviderFactory().Descriptor.Settings.ToDictionary(f => f.Key);

        Assert.Equal(1.0, fields["interval"].Default!.GetValue<double>());
        Assert.Equal(SettingKind.TextList, fields["include"].Kind);
        Assert.Equal(SettingKind.Boolean, fields["skipDuplicates"].Kind);
        Assert.True(fields["skipDuplicates"].Default!.GetValue<bool>());
        Assert.Equal(3, fields.Count);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Descriptor_serializes_with_the_shared_json_options(string type)
    {
        var json = JsonSerializer.Serialize(Factory(type).Descriptor, Json.Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(type, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("windows", doc.RootElement.GetProperty("platforms").GetString());
        Assert.Equal("thisPc", doc.RootElement.GetProperty("group").GetString());
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Create_off_windows_throws_platform_not_supported(string type)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() =>
            Factory(type).Create(new ProviderInstanceConfig { Id = type, Type = type }, new Services()));
    }

    [Fact]
    public void Hwinfo_messages_are_bilingual()
    {
        foreach (var message in new[]
        {
            HwInfoMessages.NotRunning, HwInfoMessages.AccessDenied, HwInfoMessages.Stale, HwInfoMessages.ShuttingDown,
            HwInfoMessages.Empty, HwInfoMessages.BadData, HwInfoMessages.Unstable,
        })
        {
            AssertBilingual(message, message.En);
        }
    }

    private static void AssertBilingual(LocalizedText text, string what, bool requireTranslation = true)
    {
        Assert.False(string.IsNullOrWhiteSpace(text.En), what);
        if (requireTranslation)
        {
            Assert.False(string.IsNullOrWhiteSpace(text.It), what + " (it)");
            if (text.En.Length > 4)
            {
                Assert.NotEqual(text.En, text.It); // short technical labels such as CPU stay the same
            }
        }
    }

    private sealed class Services : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
