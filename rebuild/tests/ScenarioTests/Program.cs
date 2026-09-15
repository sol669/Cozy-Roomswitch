using RoomSwitcherTray.Core;
using RoomSwitcherTray.Core.Services;
using System.Diagnostics;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
ScenarioDefinition Scenario(string name, string[] screens, string audio = "sound") =>
    new() { Name = name, DisplayIds = screens.ToList(), AudioDeviceId = audio, IconLetters = "PC", VolumePercent = 73 };
DisplayDevice Screen(string id, bool connected = true, bool active = true, Guid? container = null) =>
    new(id, id, active, connected, container);
AudioDevice Sound(string id = "sound", bool connected = true, bool current = true, Guid? container = null) =>
    new(id, id, current, connected ? AudioDeviceState.Active : AudioDeviceState.Unplugged, container,
        container.HasValue ? AudioDeviceKind.Display : AudioDeviceKind.Other);
DeviceSnapshot Snapshot(DisplayDevice[] screens, params AudioDevice[] audio) =>
    new(screens, audio, [], audio.Any(item => item.IsActive && item.IsDefault) ? new("sound", 20, false) : null);
async Task Until(Func<bool> done)
{
    for (int i = 0; i < 100 && !done(); i++) await Task.Delay(10);
    if (!done()) throw new Exception("Timed out waiting for test continuation.");
}
(FakeDevices devices, AppSettings settings, ScenarioCoordinator coordinator) Fixture(
    DeviceSnapshot snapshot, ScenarioDefinition[] scenarios, Guid? active = null, int timeoutMs = 300)
{
    var devices = new FakeDevices(snapshot);
    var settings = new AppSettings { Scenarios = scenarios.ToList(), ActiveScenarioId = active };
    ScenarioCoordinator? coordinator = null;
    coordinator = new ScenarioCoordinator(() => settings, () => { devices.Saves++; coordinator?.SettingsChanged(); }, devices.Errors.Add,
        devices, TimeSpan.FromMilliseconds(timeoutMs));
    return (devices, settings, coordinator);
}

var office = Scenario("Кабинет", ["main", "second"]);
var living = Scenario("Гостиная", ["tv"], "hdmi");
var laptop = Scenario("Ноутбук", ["laptop"], "");
var basic = Snapshot([Screen("main"), Screen("second", false, false), Screen("tv", false, false)], Sound());
Check(ScenarioPolicy.CanApply(office, basic), "one connected screen permits a two-screen scenario");
Check(!ScenarioPolicy.CanApply(living, basic), "zero connected scenario screens blocks switching");
Check(ScenarioPolicy.CanApply(office, Snapshot([Screen("main", true, false)])),
    "connected but inactive monitor remains switchable");
Check(!ScenarioPolicy.CanApply(office, Snapshot([Screen("main", false, true)])),
    "stale active flag never overrides physical disconnection");
var menuSettings = new AppSettings { Scenarios = [office, living, laptop], ActiveScenarioId = office.Id };
Check(ScenarioPolicy.Next(menuSettings, basic) is null, "no alternate available scenario: no hotkey target");
Check(ScenarioPolicy.Next(menuSettings, Snapshot([Screen("main"), Screen("laptop")]))?.Id == laptop.Id,
    "hotkey skips unavailable middle scenario");
living.IncludeInHotkeyList = false;
Check(ScenarioPolicy.Next(new AppSettings { Scenarios = [office, living, laptop], ActiveScenarioId = office.Id },
    Snapshot([Screen("main"), Screen("tv"), Screen("laptop")]))?.Id == laptop.Id,
    "hotkey skips an available scenario excluded from the hotkey list");
Check(!living.IncludeInHotkeyList && ScenarioPolicy.TrayOrder(new AppSettings { Scenarios = [office, living, laptop] })
    .Any(item => item.Scenario.Id == living.Id), "excluded scenario remains in the tray list");
Check(JsonSerializer.Deserialize<ScenarioDefinition>("{\"Name\":\"Legacy\"}")!.IncludeInHotkeyList,
    "legacy settings default every scenario into the hotkey list");
living.IncludeInHotkeyList = true;
menuSettings.ActiveScenarioId = laptop.Id;
Check(ScenarioPolicy.Next(menuSettings, Snapshot([Screen("main"), Screen("laptop")]))?.Id == office.Id,
    "hotkey wraps to available partial scenario");
menuSettings.Scenarios = [office]; menuSettings.ActiveScenarioId = office.Id;
Check(ScenarioPolicy.Next(menuSettings, basic) is null, "one current scenario: hotkey is a no-op");
Check(laptop.IsComplete, "audio None does not prevent a valid video-only scenario");

{
    var allDevices = Snapshot([Screen("main"), Screen("second", false, false), Screen("tv")],
        Sound("fallback"), Sound("hdmi", true, false));
    var rows = ScenarioPolicy.TrayDevices(office, allDevices);
    Check(rows.DisplayIds.SequenceEqual(["main", "second"]),
        "tray includes only selected scenario monitors, including the unplugged one");
    Check(rows.AudioId == "sound" && rows.Audio is null,
        "missing scenario card remains a row; current Windows fallback is not substituted");
    Check(!rows.DisplayIds.Contains("tv") && rows.AudioId != "hdmi",
        "other scenario's television and HDMI audio never leak into the office tray");
    var livingRows = ScenarioPolicy.TrayDevices(living, allDevices);
    Check(livingRows.DisplayIds.SequenceEqual(["tv"]) && livingRows.Audio?.Id == "hdmi",
        "switching scenario replaces the entire tray device scope");
    Check(ScenarioPolicy.TrayDevices(laptop, allDevices).AudioId is null,
        "video-only scenario has no audio row even if Windows has a default output");
    var noScenario = ScenarioPolicy.TrayDevices(null, allDevices);
    Check(noScenario.DisplayIds.Count == 0 && noScenario.AudioId is null,
        "no selected scenario does not become a global Windows device list");
    var cardDisconnected = allDevices with { Audio = [Sound("sound", false, false), Sound("fallback")] };
    var offlineRows = ScenarioPolicy.TrayDevices(office, cardDisconnected);
    Check(offlineRows.Audio?.Id == "sound" && offlineRows.Audio.State == AudioDeviceState.Unplugged,
        "configured unplugged audio is retained instead of the live fallback output");
    var failedRead = allDevices with { AudioReadFailed = true };
    Check(ScenarioPolicy.TrayDevices(office, failedRead).AudioId == "sound" &&
        ScenarioPolicy.TrayDevices(office, failedRead).Audio is null,
        "audio query failure retains the configured row without inventing a replacement");
    Check(ScenarioPolicy.CanApply(office, failedRead), "audio query failure never blocks a scenario with a screen");
    Check(ScenarioPolicy.Evaluate(Scenario("Single", ["main"]), failedRead, menuSettings).Reason == "Не удалось проверить звук",
        "audio query failure is distinguished from physical disconnection");
    var (devices, settings, coordinator) = Fixture(failedRead, [office], office.Id);
    using (coordinator)
    {
        Check((await coordinator.ApplyAsync(office.Id)).Success && coordinator.HasReliableSnapshot,
            "audio read failure still applies video and preserves switch availability");
        Check(devices.AudioCalls.Count == 0, "audio read failure never writes to Windows fallback");
    }
}

{
    var (devices, settings, coordinator) = Fixture(basic, [office, living]);
    using (coordinator)
    {
        var result = await coordinator.ApplyAsync(office.Id);
        Check(result.Success && settings.ActiveScenarioId == office.Id, "startup applies available subset and selects scenario");
        Check(devices.VideoCalls.Single().SequenceEqual(["main"]), "never submits disconnected monitor to Windows");
        Check(coordinator.Status.Health == ScenarioHealth.Partial, "partial application produces warning");
        Check(office.DisplayIds.SequenceEqual(["main", "second"]), "saved display bindings are not rewritten");
        Check(devices.AudioCalls.Single() == ("sound", (int?)73), "volume is bound to intended audio endpoint");
        int calls = devices.VideoCalls.Count;
        devices.Current = Snapshot([Screen("main"), Screen("second", true, false)], Sound());
        await coordinator.RefreshAsync();
        Check(devices.VideoCalls.Count == calls && coordinator.Status.Warn, "reconnection does not apply topology; inactive screen keeps warning");
        devices.Current = Snapshot([Screen("main"), Screen("second")], Sound());
        await coordinator.RefreshAsync();
        Check(coordinator.Status.Health == ScenarioHealth.Full, "actual complete topology clears warning");
        devices.Current = Snapshot([Screen("main")], Sound());
        await coordinator.RefreshAsync();
        Check(coordinator.Status.Warn && devices.VideoCalls.Count == calls, "unplugging only updates status");
    }
}
{
    var (devices, settings, coordinator) = Fixture(basic, [office, living], office.Id);
    using (coordinator)
    {
        Check(!(await coordinator.ApplyAsync(living.Id)).Success, "unavailable manual/startup attempt fails without changes");
        Check(devices.VideoCalls.Count == 0 && devices.AudioCalls.Count == 0, "blocked attempt changes neither screens nor sound");
        Check(settings.ActiveScenarioId == office.Id && coordinator.DesiredScenario?.Id == living.Id,
            "failed desired scenario is separate from previous active scenario");
        Check(coordinator.Status.Health == ScenarioHealth.Failed &&
            ScenarioPolicy.Tooltip(coordinator.DesiredScenario, coordinator.Status, false) == "Сценарий: Гостиная\nНе удалось применить сценарий",
            "warning tooltip identifies failed target, not previous scenario");
    }
}
{
    var scenario = Scenario("No card", ["main"]);
    var (devices, settings, coordinator) = Fixture(Snapshot([Screen("main")], Sound("fallback")), [scenario]);
    using (coordinator)
    {
        Check((await coordinator.ApplyAsync(scenario.Id)).Success, "missing sound does not reject video application");
        Check(devices.AudioCalls.Count == 0 && !coordinator.IsWaitingForAudio, "no fallback volume write and no pointless non-HDMI wait");
        Check(coordinator.Status.Warn, "missing audio still produces a warning");
    }
}
{
    var scenario = Scenario("No outputs", ["main"], "missing");
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("main")]), [scenario]);
    using (coordinator)
    {
        Check((await coordinator.ApplyAsync(scenario.Id)).Success && coordinator.Status.Warn,
            "no audio outputs at all is supported");
    }
}
{
    var scenario = Scenario("Audio failure", ["main"]);
    var (devices, settings, coordinator) = Fixture(Snapshot([Screen("main")], Sound()), [scenario]);
    devices.ThrowAudio = true;
    using (coordinator)
    {
        Check((await coordinator.ApplyAsync(scenario.Id)).Success && coordinator.Status.Warn,
            "audio API failure produces partial state, not video failure");
        Check(devices.VideoCalls.Count == 1 && settings.ActiveScenarioId == scenario.Id, "audio failure never rolls back valid video");
    }
}
Guid hdmiContainer = Guid.NewGuid();
{
    var scenario = Scenario("TV", ["tv"], "old-endpoint");
    scenario.AudioDeviceContainerId = hdmiContainer.ToString();
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("tv", true, false, hdmiContainer)]), [scenario], timeoutMs: 1500);
    using (coordinator)
    {
        await coordinator.ApplyAsync(scenario.Id);
        Check(coordinator.IsWaitingForAudio && !coordinator.IsApplying && !coordinator.Status.Warn,
            "HDMI wait is bounded, nonblocking and suppresses transient warning");
        devices.Current = Snapshot([Screen("tv", true, true, hdmiContainer)], Sound("new-endpoint", true, false, hdmiContainer));
        await coordinator.RefreshAsync();
        await Until(() => !coordinator.IsWaitingForAudio);
        Check(devices.AudioCalls.Single().Id == "new-endpoint", "hardware event wakes HDMI wait with stable container matching");
        Check(scenario.AudioDeviceId == "new-endpoint" && coordinator.Status.Health == ScenarioHealth.Full,
            "new HDMI endpoint identity is remembered and verified");
    }
}
{
    var tv = Scenario("TV", ["tv"], "hdmi"); tv.AudioDeviceContainerId = hdmiContainer.ToString();
    var pc = Scenario("PC", ["main"]);
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("tv", true, false, hdmiContainer), Screen("main")], Sound()), [tv, pc], timeoutMs: 1500);
    using (coordinator)
    {
        await coordinator.ApplyAsync(tv.Id);
        Check(coordinator.IsWaitingForAudio, "old scenario has pending HDMI audio");
        await coordinator.ApplyAsync(pc.Id);
        devices.Current = Snapshot([Screen("tv", true, false, hdmiContainer), Screen("main")], Sound(), Sound("hdmi", true, false, hdmiContainer));
        await coordinator.RefreshAsync();
        await Task.Delay(30);
        Check(devices.AudioCalls.Count == 1 && devices.AudioCalls[0].Id == "sound",
            "switching cancels stale HDMI wait; late device cannot steal audio");
    }
}
{
    var tv = Scenario("TV timeout", ["tv"], "hdmi"); tv.AudioDeviceContainerId = hdmiContainer.ToString();
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("tv", true, true, hdmiContainer)]), [tv], timeoutMs: 90);
    using (coordinator)
    {
        await coordinator.ApplyAsync(tv.Id);
        int reads = devices.Captures;
        await Until(() => !coordinator.IsWaitingForAudio);
        Check(coordinator.Status.Warn && devices.Captures == reads, "HDMI timeout produces warning with zero periodic hardware reads");
        devices.Current = Snapshot([Screen("tv", true, true, hdmiContainer)], Sound("hdmi", true, false, hdmiContainer));
        await coordinator.RefreshAsync();
        Check(devices.AudioCalls.Count == 0, "reconnect after timeout never silently redirects sound");
    }
}
{
    var scenario = Scenario("Broken apply", ["tv"], "");
    var (devices, settings, coordinator) = Fixture(Snapshot([Screen("main"), Screen("tv", true, false)]), [office, scenario], office.Id);
    devices.FailNextVideo = true;
    using (coordinator)
    {
        Check(!(await coordinator.ApplyAsync(scenario.Id)).Success, "display API rejection is reported");
        Check(devices.VideoCalls.Count == 2 && devices.VideoCalls[1].SequenceEqual(["main"]), "display rejection rolls back prior connected screen");
        Check(settings.ActiveScenarioId == office.Id && coordinator.Status.Health == ScenarioHealth.Failed, "rollback never marks failed target active");
    }
}
{
    var scenario = Scenario("Memory", ["main"], "");
    var (devices, settings, coordinator) = Fixture(Snapshot([Screen("main")]), [scenario]);
    using (coordinator)
    {
        await coordinator.RefreshAsync();
        int saves = devices.Saves;
        for (int i = 0; i < 500; i++) await coordinator.RefreshAsync();
        Check(devices.Saves == saves, "unchanged snapshots never rewrite settings");
        int reads = devices.Captures;
        await Task.Delay(100);
        Check(devices.Captures == reads, "idle coordinator has zero spontaneous hardware queries");
        settings.DeviceAliases["main"] = "My monitor";
        Check(ScenarioPolicy.Name(settings, DeviceSnapshot.Empty, "main", "?") == "My monitor", "offline aliases survive");
        Check(settings.KnownDeviceNames["main"] == "main", "last system name retained independently");
        string json = JsonSerializer.Serialize(settings);
        Check(JsonSerializer.Deserialize<AppSettings>(json)!.Scenarios[0].DisplayIds[0] == "main", "settings schema round-trip preserves identities");
    }
}
{
    var scenario = Scenario(new string('Я', 80), ["one", "two"], "audio");
    var settings = new AppSettings { KnownDeviceNames = { ["one"] = new string('Ж', 100), ["two"] = "Second" } };
    string tip = ScenarioPolicy.Tooltip(scenario, ScenarioPolicy.Evaluate(scenario, DeviceSnapshot.Empty, settings), false);
    Check(tip.Length <= 127 && tip.Contains("Не все устройства подключены"), "native tooltip length limit preserves meaningful status");
    var legacy = new[] { ScenarioIcon.Letters, ScenarioIcon.Desktop, ScenarioIcon.Television, ScenarioIcon.Sofa, ScenarioIcon.Gamepad };
    Check(legacy.Select(item => (int)item).SequenceEqual([0, 1, 2, 3, 4]), "new state icons never alter saved scenario icon IDs");
}

{
    var scenario = Scenario("Audio service offline", ["main"]);
    var brokenAudio = Snapshot([Screen("main")]) with { AudioReadFailed = true };
    var (devices, _, coordinator) = Fixture(brokenAudio, [scenario]);
    using (coordinator)
    {
        Check((await coordinator.ApplyAsync(scenario.Id)).Success && devices.AudioCalls.Count == 0,
            "audio enumeration failure cannot block video");
        Check(coordinator.Status.Reason.Contains("проверить звук"), "audio query failure is distinct from physical disconnection");
    }
}
{
    var disabled = Sound() with { State = AudioDeviceState.Disabled, IsDefault = false };
    Check(ScenarioPolicy.Evaluate(office, Snapshot([Screen("main"), Screen("second")], disabled), new()).Reason.Contains("Windows"),
        "disabled in Windows is distinct from an unplugged device");
    var ambiguous = Snapshot([Screen("tv", true, true, hdmiContainer)], Sound("a", true, false, hdmiContainer), Sound("b", true, false, hdmiContainer));
    var scenario = Scenario("Ambiguous", ["tv"], "missing");
    scenario.AudioDeviceContainerId = hdmiContainer.ToString();
    Check(ScenarioPolicy.FindAudio(scenario, ambiguous) is null, "ambiguous container never picks a random audio output");
    var driverAudio = Sound("missing", false, false, Guid.NewGuid());
    Check(ScenarioPolicy.MayAwaitHdmi(scenario, Snapshot([Screen("tv")], driverAudio)),
        "HDMI with driver-specific container still gets event-based wait");
    Check(!ScenarioPolicy.MayAwaitHdmi(scenario, Snapshot([Screen("tv", true, true, hdmiContainer)],
        driverAudio with { State = AudioDeviceState.Disabled })), "disabled endpoint never starts pointless HDMI wait");
}
{
    var scenario = Scenario("Read failure", ["main"], "");
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("main")]), [scenario], scenario.Id);
    using (coordinator)
    {
        await coordinator.RefreshAsync();
        devices.FailCapture = true;
        try { await coordinator.RefreshAsync(); } catch (InvalidOperationException) { }
        Check(!coordinator.HasReliableSnapshot && coordinator.Status.Warn, "failed refresh invalidates stale availability");
        Check(!(await coordinator.ApplyAsync(scenario.Id)).Success && devices.VideoCalls.Count == 0,
            "failed preflight read never changes displays");
    }
}
{
    var scenario = Scenario("Edited wait", ["tv"], "hdmi"); scenario.AudioDeviceContainerId = hdmiContainer.ToString();
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("tv", true, true, hdmiContainer)]), [scenario], timeoutMs: 1500);
    using (coordinator)
    {
        await coordinator.ApplyAsync(scenario.Id);
        scenario.Icon = ScenarioIcon.Sofa;
        coordinator.SettingsChanged();
        Check(coordinator.IsWaitingForAudio, "cosmetic settings edit does not cancel pending HDMI");
        scenario.AudioDeviceId = "different";
        coordinator.SettingsChanged();
        Check(!coordinator.IsWaitingForAudio, "operational settings edit cancels stale pending HDMI");
        devices.Current = Snapshot([Screen("tv", true, true, hdmiContainer)], Sound("hdmi", true, false, hdmiContainer));
        await coordinator.RefreshAsync();
        await Task.Delay(20);
        Check(devices.AudioCalls.Count == 0, "late endpoint cannot apply an edited-away audio choice");
    }
}
{
    var scenario = Scenario("Dispose", ["tv"], "hdmi"); scenario.AudioDeviceContainerId = hdmiContainer.ToString();
    var (devices, _, coordinator) = Fixture(Snapshot([Screen("tv", true, true, hdmiContainer)]), [scenario], timeoutMs: 1500);
    await coordinator.ApplyAsync(scenario.Id);
    coordinator.Dispose();
    int reads = devices.Captures;
    await coordinator.RefreshAsync();
    Check(!coordinator.IsWaitingForAudio && devices.Captures == reads, "dispose cancels wait and stops future device queries");
}
{
    var scenario = Scenario("Deleted", ["tv"], "");
    var (_, settings, coordinator) = Fixture(basic, [office, scenario], office.Id);
    using (coordinator)
    {
        await coordinator.ApplyAsync(scenario.Id);
        settings.Scenarios.Remove(scenario);
        coordinator.SettingsChanged();
        Check(coordinator.DesiredScenario?.Id == office.Id && coordinator.Status.Health != ScenarioHealth.Failed,
            "deleting failed target returns status to remaining active scenario");
    }
}
// Startup volume is an apply-time instruction, never a continuously enforced target.
{
    var fresh = new ScenarioDefinition();
    var input = new StartupVolumeInput(fresh.VolumePercent);
    Check(fresh.VolumePercent is null && !input.Enabled && input.IsValid && input.Value is null,
        "new scenario defaults to do-not-change volume with hidden numeric input");
    input.Enabled = true;
    Check(!input.IsValid, "enabling volume requires an explicit numeric value");
    foreach (int value in new[] { 0, 1, 27, 63, 99, 100 })
    {
        input.Text = value.ToString();
        Check(input.IsValid && input.Value == value, $"free whole percentage {value} is accepted");
    }
    foreach (string text in new[] { "", " ", "-1", "101", "999999999999", "27.5", "27,5", "+10", "10%", "abc", "1e2" })
    {
        input.Text = text;
        Check(!input.IsValid, $"invalid percentage '{text}' cannot be saved");
    }
    input.Enabled = false;
    Check(input.IsValid && input.Value is null, "do-not-change hides and ignores incomplete numeric input");
    foreach (int? saved in new int?[] { null, 0, 10, 73, 100 })
    {
        var restored = new StartupVolumeInput(saved);
        Check(restored.IsValid && restored.Value == saved && restored.Enabled == saved.HasValue,
            $"legacy saved volume {saved?.ToString() ?? "null"} keeps its meaning");
    }
}
{
    var scenario = Scenario("Volume once", ["main"]);
    scenario.VolumePercent = 100;
    var (devices, settings, coordinator) = Fixture(Snapshot([Screen("main")], Sound()), [scenario]);
    using (coordinator)
    {
        await coordinator.ApplyAsync(scenario.Id);
        Check(devices.AudioCalls.Single().Volume == 100, "scenario applies configured startup volume once");
        int saves = devices.Saves;
        foreach (var state in new[] { new AudioEndpointStatus("sound", 27, false), new("sound", 0, true), new("sound", 63, false) })
        {
            devices.Current = devices.Current with { DefaultAudio = state };
            await coordinator.RefreshAsync();
            Check(coordinator.Status.Health == ScenarioHealth.Full && !coordinator.Status.Warn,
                $"manual volume/mute change {state.VolumePercent}/{state.IsMuted} never produces warning");
        }
        Check(devices.AudioCalls.Count == 1 && devices.Saves == saves && scenario.VolumePercent == 100,
            "manual volume changes neither reapply audio nor overwrite saved percentage");
        Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.Scenarios.Single().VolumePercent == 100,
            "saved percentage survives serialization independently of live volume");
        await coordinator.ApplyAsync(scenario.Id);
        Check(devices.AudioCalls.Count == 2 && devices.AudioCalls.Last().Volume == 100,
            "next explicit scenario application reapplies saved percentage");
        scenario.VolumePercent = null;
        await coordinator.ApplyAsync(scenario.Id);
        Check(devices.AudioCalls.Last().Volume is null, "do-not-change passes no volume write to backend");
    }
}
{
    var scenario = Scenario("Кабинет", ["main"]);
    Check(ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Full, ""), false) == "Сценарий: Кабинет",
        "normal tooltip identifies scenario explicitly");
    Check(ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Full, ""), true) == "Scenario: Кабинет",
        "scenario tooltip prefix is localized");
    Check(ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Partial, "detail", ScenarioIssue.DevicesMissing), false) ==
        "Сценарий: Кабинет\nНе все устройства подключены", "partial tooltip is concise");
    Check(ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Partial, "detail", ScenarioIssue.DevicesCheckFailed), false).EndsWith(
        "Не удалось проверить устройства"), "query failure is not described as unplugging");
    Check(ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Checking, ""), false).EndsWith("Переключение сценария"),
        "transition is described without an error warning");
    Check(ScenarioPolicy.Tooltip(null, new(ScenarioHealth.None, ""), false) == "Cozy Roomswitch", "unconfigured tooltip keeps app name");
    scenario.Name = new string('A', 62) + "😀" + new string('B', 40);
    string tip = ScenarioPolicy.Tooltip(scenario, new(ScenarioHealth.Failed, ""), false);
    Check(!tip.Contains('\uD83D') && tip.EndsWith("Не удалось применить сценарий") && tip.Length <= 127,
        "long scenario name truncation preserves Unicode and leaves room for warning");
}
{
    Guid common = new("00000000-0000-0000-ffff-ffffffffffff");
    Guid physical = Guid.NewGuid();
    foreach (Guid container in new[] { common, physical })
    {
        var scenario = Scenario("Audio identity", ["panel"], "speakers");
        scenario.AudioDeviceContainerId = container.ToString();
        var speakers = Sound("speakers", false, false, container) with { Kind = AudioDeviceKind.Other };
        var headphones = Sound("headphones", true, true, container) with { Kind = AudioDeviceKind.Other };
        var snapshot = Snapshot([Screen("panel", true, true, container)], speakers, headphones);
        Check(ScenarioPolicy.FindAudio(scenario, snapshot) is null, "disconnected exact endpoint cannot resolve to active sibling");
        Check(ScenarioPolicy.FindAudio(scenario, snapshot, false)?.Id == "speakers", "offline status retains exact configured endpoint");
        Check(!ScenarioPolicy.MayAwaitHdmi(scenario, snapshot), "non-HDMI output does not start HDMI wait through shared screen container");
        scenario.AudioDeviceId = "missing";
        Check(ScenarioPolicy.FindAudio(scenario, snapshot) is null, "inactive sibling counts when assessing container ambiguity");
        var (devices, settings, coordinator) = Fixture(snapshot, [scenario]);
        using (coordinator)
        {
            await coordinator.ApplyAsync(scenario.Id);
            Check(devices.AudioCalls.Count == 0 && scenario.AudioDeviceId == "missing",
                "ambiguous audio neither receives volume nor rewrites saved binding");
        }
    }
    var missing = Scenario("Shared", ["panel"], "missing");
    foreach (Guid container in new[] { common, Guid.Empty })
    {
        missing.AudioDeviceContainerId = container.ToString();
        Check(ScenarioPolicy.FindAudio(missing, Snapshot([Screen("panel")], Sound("only", true, true, container))) is null,
            "system/empty container is not an identity even if only one endpoint currently exists");
    }
    missing.AudioDeviceContainerId = physical.ToString();
    Check(ScenarioPolicy.FindAudio(missing, Snapshot([Screen("panel")], Sound("replacement", true, true, physical)))?.Id == "replacement",
        "one unambiguous physical container still supports endpoint renewal");
    missing.AudioDeviceId = "";
    Check(ScenarioPolicy.FindAudio(missing, Snapshot([Screen("panel")], Sound("replacement", true, true, physical))) is null,
        "audio None cannot resolve a stale container binding");
}
{
    var a = Scenario("A", ["screen"], "");
    var b = Scenario("B", ["screen"], "");
    var c = Scenario("C", ["screen"], "");
    var settings = new AppSettings { Scenarios = [a, b, c], ActiveScenarioId = b.Id };
    var order = ScenarioPolicy.TrayOrder(settings).ToList();
    Check(order.Select(item => item.Scenario.Id).SequenceEqual([b.Id, a.Id, c.Id]), "active scenario moves first; remaining order stays stable");
    Check(order.Select(item => item.Index).SequenceEqual([1, 0, 2]), "menu commands retain original scenario indices after sorting");
    Check(settings.Scenarios.Select(item => item.Id).SequenceEqual([a.Id, b.Id, c.Id]), "tray ordering does not modify saved scenario order");
    Check(ScenarioPolicy.Next(settings, Snapshot([Screen("screen")]))?.Id == c.Id, "next hotkey keeps original cycle after tray reorder");
    settings.ActiveScenarioId = null;
    Check(ScenarioPolicy.TrayOrder(settings).Select(item => item.Index).SequenceEqual([0, 1, 2]), "no active scenario retains saved menu order");
}
await AudioMigrationTests.Run();
foreach (var language in new[] { AppLanguage.Russian, AppLanguage.English })
{
    var preferences = new AppSettings { Language = language };
    Check(UiText.AudioLevel(preferences, new("output", 100, true)) == UiText.Get(preferences, "Muted"),
        "muted tray status contains no percentage in either language");
    Check(UiText.AudioLevel(preferences, new("output", 27, false)) == "27%", "unmuted tray status retains current percentage");
}
Console.WriteLine($"ALL {checks} CHECKS PASSED. No Windows devices or user settings were changed.");

sealed class FakeDevices(DeviceSnapshot current) : IScenarioDevices
{
    public DeviceSnapshot Current = current;
    public int Captures, Saves;
    public List<string[]> VideoCalls = [];
    public List<(string Id, int? Volume)> AudioCalls = [];
    public List<Exception> Errors = [];
    public bool ThrowAudio, FailNextVideo, FailCapture;
    public Task<DeviceSnapshot> CaptureAsync()
    {
        Captures++;
        if (FailCapture) throw new InvalidOperationException("Simulated device query failure");
        return Task.FromResult(Current);
    }
    public Task ApplyDisplaysAsync(IReadOnlyCollection<string> ids)
    {
        VideoCalls.Add(ids.ToArray());
        if (FailNextVideo) { FailNextVideo = false; throw new InvalidOperationException("Simulated display rejection"); }
        Current = Current with { Displays = Current.Displays.Select(item =>
            item with { IsActive = ids.Contains(item.Id) && item.IsAvailable }).ToArray() };
        return Task.CompletedTask;
    }
    public Task ApplyResolutionsAsync(ScenarioDefinition scenario) => Task.CompletedTask;
    public void ApplyAudio(AudioDevice device, int? volume)
    {
        AudioCalls.Add((device.Id, volume));
        if (ThrowAudio) throw new InvalidOperationException("Simulated audio rejection");
        Current = Current with { Audio = Current.Audio.Select(item => item with { IsDefault = item.Id == device.Id }).ToArray() };
    }
}
