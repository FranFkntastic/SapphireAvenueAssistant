using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SapphireAvenueRelay;

internal sealed class RelayConfigurationWindow : Window, IDisposable
{
    private static readonly Vector4 GoodColor = new(0.35f, 0.82f, 0.55f, 1f);
    private static readonly Vector4 MutedColor = new(0.62f, 0.65f, 0.70f, 1f);
    private static readonly Vector4 ErrorColor = new(0.95f, 0.45f, 0.42f, 1f);
    private readonly RelayConfiguration configuration;
    private readonly CwlsRelayWorker worker;
    private readonly CancellationTokenSource cancellation = new();
    private string connectionString = string.Empty;
    private Task<RelayPairingResult>? pairingTask;
    private string? actionMessage;
    private bool actionFailed;

    public RelayConfigurationWindow(RelayConfiguration configuration, CwlsRelayWorker worker)
        : base("Sapphire Avenue Discord Bridge##SapphireAvenueDiscordBridge")
    {
        this.configuration = configuration;
        this.worker = worker;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(510f, 430f),
            MaximumSize = new Vector2(760f, 720f),
        };
    }

    public override void Draw()
    {
        CompletePairingIfReady();
        var snapshot = worker.CreateSnapshot();
        var display = RelayConfigurationPolicy.Describe(snapshot);

        ImGui.Text("Sapphire Avenue Discord Bridge");
        ImGui.SameLine();
        ImGui.TextColored(MutedColor, "by FranFkntastic");
        ImGui.TextColored(MutedColor, "Connect one cross-world linkshell to your community's Discord.");
        ImGui.Spacing();
        DrawStatus(snapshot, display);
        ImGui.Spacing();

        DrawSectionTitle("This relay node");
        if (snapshot.CoordinatorConfigured)
            DrawPairedNode(snapshot);
        else
            DrawPairing();

        ImGui.Spacing();
        DrawCwlsSelection(snapshot);
        ImGui.Spacing();
        DrawDirections(snapshot);

        var error = actionFailed
            ? actionMessage
            : snapshot.LastError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.Spacing();
            ImGui.TextColored(ErrorColor, "Needs attention");
            ImGui.TextWrapped(error);
        }
        else if (!string.IsNullOrWhiteSpace(actionMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(GoodColor, actionMessage);
        }

        if (snapshot.CoordinatorConfigured)
        {
            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.Button("Disconnect this node"))
                ImGui.OpenPopup("Disconnect this node?");
            DrawDisconnectConfirmation();
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void DrawStatus(RelaySnapshot snapshot, RelayNodeDisplay display)
    {
        var color = display.State is RelayNodeDisplayState.PreferredActive or RelayNodeDisplayState.Active
            ? GoodColor
            : display.State == RelayNodeDisplayState.Offline
                ? ErrorColor
                : MutedColor;
        ImGui.TextColored(color, $"● {display.Label}");
        ImGui.SameLine();
        var identity = RelayConfigurationPolicy.DisplayNodeIdentity(snapshot.Character, snapshot.HomeWorld);
        ImGui.Text(identity);
        ImGui.TextColored(MutedColor, display.Detail);
    }

    private void DrawPairedNode(RelaySnapshot snapshot)
    {
        var identity = RelayConfigurationPolicy.DisplayNodeIdentity(snapshot.Character, snapshot.HomeWorld);
        DrawLabel("Node", identity);
        DrawLabel("Coordinator", configuration.CoordinatorBaseUrl);
        ImGui.TextColored(MutedColor, "Pairing credentials are protected for this Windows user and aren't shown here.");
    }

    private void DrawPairing()
    {
        ImGui.TextWrapped("In Discord, a server manager runs /bridge add-node. Paste the entire connection string here within ten minutes.");
        ImGui.Text("Connection string");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##ConnectionString", "SADB1 https://relay.example/ ONE-TIME-CODE", ref connectionString, 1024);

        var pairing = pairingTask is not null;
        if (pairing)
            ImGui.BeginDisabled();
        if (ImGui.Button(pairing ? "Pairing…" : "Pair this installation"))
        {
            actionMessage = null;
            actionFailed = false;
            pairingTask = worker.PairAsync(connectionString, cancellation.Token);
        }
        if (pairing)
            ImGui.EndDisabled();
    }

    private void DrawCwlsSelection(RelaySnapshot snapshot)
    {
        ImGui.Text("Cross-world linkshell");
        ImGui.TextColored(MutedColor, "Read from the current character; the saved name and position must both keep matching.");
        var available = snapshot.CwlsSlots.Where(slot => !string.IsNullOrWhiteSpace(slot.Name)).ToArray();
        var preview = string.IsNullOrWhiteSpace(snapshot.ExpectedCwlsName)
            ? "Select a CWLS"
            : snapshot.SlotMatches
                ? snapshot.ExpectedCwlsName
                : $"{snapshot.ExpectedCwlsName} — unavailable in its saved position";
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##CwlsSelection", preview))
        {
            for (var index = 0; index < available.Length; index++)
            {
                var slot = available[index];
                var duplicateIndex = available.Take(index).Count(candidate =>
                    string.Equals(candidate.Name, slot.Name, StringComparison.Ordinal));
                var duplicateCount = available.Count(candidate =>
                    string.Equals(candidate.Name, slot.Name, StringComparison.Ordinal));
                var label = duplicateCount > 1 ? $"{slot.Name} ({Ordinal(duplicateIndex + 1)} listed)" : slot.Name;
                var selected = snapshot.SlotMatches &&
                               snapshot.ConfiguredSlot == slot.Slot &&
                               string.Equals(snapshot.ExpectedCwlsName, slot.Name, StringComparison.Ordinal);
                if (ImGui.Selectable($"{label}##cwls-{slot.Slot}", selected))
                {
                    var failure = worker.SelectCwls(slot);
                    actionFailed = failure is not null;
                    actionMessage = failure ?? $"Selected {slot.Name}.";
                }
            }

            if (available.Length == 0)
            {
                ImGui.BeginDisabled();
                ImGui.Selectable("No CWLS memberships discovered");
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawDirections(RelaySnapshot snapshot)
    {
        ImGui.Text("Participation");
        ImGui.TextColored(MutedColor, "Choose either direction or both; exact CWLS identity is checked before every relay.");
        var observe = snapshot.ObserveToDiscordEnabled;
        var deliver = snapshot.DiscordToGameEnabled;
        if (ImGui.Checkbox("Game → Discord", ref observe))
            ApplyDirections(observe, deliver);
        ImGui.SameLine();
        if (ImGui.Checkbox("Discord → Game", ref deliver))
            ApplyDirections(observe, deliver);
    }

    private void ApplyDirections(bool observe, bool deliver)
    {
        var failure = worker.SetDirections(observe, deliver);
        actionFailed = failure is not null;
        actionMessage = failure ?? "Relay participation updated.";
    }

    private void CompletePairingIfReady()
    {
        if (pairingTask is not { IsCompleted: true } completed)
            return;

        pairingTask = null;
        try
        {
            var pairing = completed.GetAwaiter().GetResult();
            worker.ApplyPairing(pairing);
            connectionString = string.Empty;
            actionFailed = false;
            actionMessage = "Paired. Log in to report this node's character and home world; relay directions remain disabled.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            actionMessage = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or HttpRequestException)
        {
            actionFailed = true;
            actionMessage = exception.Message;
        }
    }

    private void DrawDisconnectConfirmation()
    {
        if (!ImGui.BeginPopupModal("Disconnect this node?"))
            return;

        ImGui.TextWrapped("This clears the local pairing credential, CWLS selection, and relay participation. Reconnecting requires a new connection string from Discord.");
        if (ImGui.Button("Disconnect and clear"))
        {
            worker.ClearConfiguration();
            connectionString = string.Empty;
            actionFailed = false;
            actionMessage = "This installation was disconnected and cleared.";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.Text(title);
        ImGui.Separator();
    }

    private static void DrawLabel(string label, string value)
    {
        ImGui.TextColored(MutedColor, $"{label}:");
        ImGui.SameLine(120f);
        ImGui.TextWrapped(value);
    }

    private static string Ordinal(int value) => value switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        _ => $"#{value}",
    };
}
