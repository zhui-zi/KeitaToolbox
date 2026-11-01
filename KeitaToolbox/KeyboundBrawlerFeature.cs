using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed unsafe class KeyboundBrawlerFeature : IDisposable
{
    private const uint KeyboundBrawlerTerritory = 1360;
    private long nextSubmitAt;
    private string lastSubmittedWord = string.Empty;
    private int disposeState;

    public void Dispose() => System.Threading.Interlocked.Exchange(ref disposeState, 1);

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("自动游玩魔光键影", ImGuiTreeNodeFlags.None))
            return;

        if (Plugin.DrawFeatureToggle(
                "自动游玩魔光键影",
                Plugin.Config.Features.AutoKeyboundBrawler,
                value => Plugin.Config.Features.AutoKeyboundBrawler = value) &&
            !Plugin.Config.Features.AutoKeyboundBrawler)
        {
            lastSubmittedWord = string.Empty;
        }

        var autoStart = Plugin.Config.Features.AutoStartKeyboundBrawler;
        if (ImGui.Checkbox("自动开始魔光键影", ref autoStart))
        {
            Plugin.Config.Features.AutoStartKeyboundBrawler = autoStart;
            Plugin.Config.Save();
        }

        Plugin.DrawHelp("自动开始会在魔光键影区域自动报名并进入副本；自动游玩会读取词条并提交输入。两项可分别启用。");
        ImGui.TextDisabled(Plugin.Config.Features.AutoKeyboundBrawler ? "状态：已启用" : "状态：未启用");
    }

    public void Update()
    {
        if (Plugin.ClientState.TerritoryType != KeyboundBrawlerTerritory)
            return;

        if (Plugin.Config.Features.AutoStartKeyboundBrawler)
            TryRegister();

        if (!Plugin.Config.Features.AutoKeyboundBrawler || Environment.TickCount64 < nextSubmitAt)
            return;

        var wordAddon = GetAddon("KTGEWord");
        var inputAddon = GetAddon("KTGInput");
        if (wordAddon == null || inputAddon == null || !wordAddon->IsReady || !inputAddon->IsReady)
        {
            lastSubmittedWord = string.Empty;
            return;
        }

        var word = FindNextWord(wordAddon);
        if (string.IsNullOrWhiteSpace(word) || string.Equals(word, lastSubmittedWord, StringComparison.Ordinal))
            return;

        var node = inputAddon->GetNodeById(7);
        if (node == null || (int)node->Type < 1000)
            return;

        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null)
            return;

        var input = (AtkComponentTextInput*)componentNode->Component;
        input->SetText(word);
        inputAddon->FireCallbackInt(0);
        lastSubmittedWord = word;
        nextSubmitAt = Environment.TickCount64 + 180;
    }

    private void TryRegister()
    {
        if (Environment.TickCount64 < _nextRegistrationAt ||
            !Plugin.ClientState.IsLoggedIn ||
            Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51] ||
            Plugin.Condition[ConditionFlag.OccupiedInEvent] ||
            Plugin.Condition[ConditionFlag.InDutyQueue] ||
            Plugin.Condition[ConditionFlag.WaitingForDutyFinder] ||
            Plugin.Condition[ConditionFlag.WaitingForDuty])
            return;

        var dialogue = GetAddon("KTGEnterDialogue");
        if (dialogue != null && dialogue->IsReady)
        {
            dialogue->FireCallbackInt(0);
            _nextRegistrationAt = Environment.TickCount64 + 3000;
            return;
        }

        new EventStartPackt(LocalPlayerState.EntityID, KeyboundBrawlerEventId).Send();
        _nextRegistrationAt = Environment.TickCount64 + 3000;
    }

    private long _nextRegistrationAt;

    private static AtkUnitBase* GetAddon(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon == null || !addon->IsVisible ? null : addon;
    }

    private const uint KeyboundBrawlerEventId = 721882;

    private static string FindNextWord(AtkUnitBase* addon)
    {
        var selected = string.Empty;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000)
                continue;

            var component = (AtkComponentNode*)node;
            if (component->Component == null)
                continue;

            var children = component->Component->UldManager;
            for (var j = 0; j < children.NodeListCount; j++)
            {
                var child = children.NodeList[j];
                if (child == null || child->Type != NodeType.Text || child->NodeId != 20 || !child->IsVisible())
                    continue;

                var text = ((AtkTextNode*)child)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text) && (selected.Length == 0 || text.Length < selected.Length))
                    selected = text.Trim();
            }
        }

        return selected;
    }
}
