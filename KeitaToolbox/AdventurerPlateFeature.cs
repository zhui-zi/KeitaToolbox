using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;

namespace KeitaToolbox;

internal sealed unsafe class AdventurerPlateFeature : IDisposable
{
    private const string AddonName = "CharaCard";
    private readonly WindowSystem windows = new("KeitaToolboxPlate");
    private readonly AdventurerPlateWindow window;
    private PlateSnapshot? latest;
    private PlateSnapshot? stashed;
    private readonly Dictionary<uint, bool> unlockCache = new();
    private Dictionary<uint, uint>? conditionIndexes;
    private long lastUnlockProbeAt;

    public AdventurerPlateFeature()
    {
        window = new AdventurerPlateWindow(this);
        windows.AddWindow(window);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnPlateEvent);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnPlateEvent);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPlateClosed);
        foreach (var addon in new[] { "BannerEditor", "CharaCardEditMenu", "CharaCardDesignSetting" })
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addon, OnUnlockEditorOpened);
        Plugin.PluginInterface.UiBuilder.Draw += windows.Draw;
        ProbeUnlocks();
    }

    public void Update()
    {
        var now = Environment.TickCount64;
        if (now - lastUnlockProbeAt < 5_000)
            return;
        ProbeUnlocks();
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("冒险者铭牌查看器"))
            return;

        Plugin.DrawFeatureToggle(
            "启用铭牌查看器",
            Plugin.Config.Features.AdventurerPlate,
            value => Plugin.Config.Features.AdventurerPlate = value);
        Plugin.DrawHelp("打开任意角色的冒险者铭牌时，读取其设计、肖像和资料，并在独立窗口中显示。不会修改对方数据。");

        ImGui.Spacing();
        ImGui.TextUnformatted("打开行为");
        ImGui.Indent();
        var autoOpen = Plugin.Config.AdventurerPlate.AutoOpen;
        if (ImGui.Checkbox("打开铭牌时自动显示查看器", ref autoOpen))
        {
            Plugin.Config.AdventurerPlate.AutoOpen = autoOpen;
            Plugin.Config.Save();
        }
        var ignoreOwn = Plugin.Config.AdventurerPlate.IgnoreOwnPlate;
        if (ImGui.Checkbox("忽略自己的铭牌", ref ignoreOwn))
        {
            Plugin.Config.AdventurerPlate.IgnoreOwnPlate = ignoreOwn;
            Plugin.Config.Save();
        }
        var pinned = Plugin.Config.AdventurerPlate.Pinned;
        if (ImGui.Checkbox("固定当前铭牌", ref pinned))
        {
            Plugin.Config.AdventurerPlate.Pinned = pinned;
            Plugin.Config.Save();
        }
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextUnformatted("当前数据");
        ImGui.Indent();
        ImGui.TextDisabled(latest == null ? "尚未捕获铭牌" : $"最近捕获：{latest.Name}");
        if (latest != null && ImGui.Button("打开最近铭牌查看器"))
            window.IsOpen = true;
        ImGui.SameLine();
        if (ImGui.Button("清除缓存"))
        {
            latest = null;
            stashed = null;
            window.Refresh(null, null);
            window.IsOpen = false;
        }
        ImGui.Unindent();

        Plugin.DrawHelp("可使用命令打开查看器；窗口中可以复制肖像预设和铭牌文本。");
        Plugin.DrawCommandHelp("/ktb plate");
        ImGui.Separator();
    }

    public bool HandleCommand(string args)
    {
        if (!args.Equals("plate", StringComparison.OrdinalIgnoreCase) &&
            !args.Equals("plate show", StringComparison.OrdinalIgnoreCase))
            return false;
        window.IsOpen = true;
        return true;
    }

    private void OnPlateEvent(AddonEvent _, AddonArgs __)
    {
        if (!Plugin.Config.Features.AdventurerPlate)
            return;
        try
        {
            var snapshot = CaptureNow();
            if (snapshot == null)
                return;

            if (latest != null && (Plugin.Config.AdventurerPlate.Pinned ||
                (snapshot.IsSelf && !latest.IsSelf)))
                stashed = snapshot;
            else
            {
                if (latest != null)
                    stashed = latest;
                latest = snapshot;
            }

            window.Refresh(latest, stashed);
            if (Plugin.Config.AdventurerPlate.AutoOpen &&
                !(Plugin.Config.AdventurerPlate.IgnoreOwnPlate && snapshot.IsSelf))
                window.IsOpen = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to capture adventurer plate.");
        }
    }

    private void OnPlateClosed(AddonEvent _, AddonArgs __)
    {
            if (!Plugin.Config.AdventurerPlate.Pinned)
            window.IsOpen = false;
    }

    private void OnUnlockEditorOpened(AddonEvent _, AddonArgs __)
        => ProbeUnlocks();

    private void ProbeUnlocks()
    {
        lastUnlockProbeAt = Environment.TickCount64;
        try
        {
            if (conditionIndexes == null)
            {
                conditionIndexes = new Dictionary<uint, uint>();
                uint index = 0;
                foreach (var row in Plugin.Data.GetExcelSheet<BannerCondition>())
                    conditionIndexes[row.RowId] = index++;
            }

            var checkedCount = 0;
            foreach (var entry in conditionIndexes)
            {
                var row = ExdModule.GetBannerConditionByIndex(entry.Value);
                if (row == null)
                    continue;
                unlockCache[entry.Key] = ExdModule.GetBannerConditionUnlockState(row) == 0;
                checkedCount++;
            }

            if (checkedCount > 0 && latest != null)
                window.Refresh(latest, stashed);
            if (checkedCount > 0)
                Plugin.Log.Debug($"Loaded {checkedCount} adventurer plate unlock conditions.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read adventurer plate unlock conditions.");
        }
    }

    private static PlateSnapshot? CaptureNow()
    {
        var agent = AgentCharaCard.Instance();
        if (agent == null || agent->Data == null || agent->Data->IsNotCreated)
            return null;

        var data = agent->Data;
        var local = Control.Instance()->LocalPlayer;
        var portrait = data->PortraitData;
        var decorations = new List<ushort>();
        foreach (var id in data->PlateDesign.Decorations)
            if (id != 0) decorations.Add(id);
        var gear = new List<uint>();
        for (var i = 0; i < 14; i++)
            if (data->CharaView.PortraitCharacterData.ItemIds[i] != 0)
                gear.Add(data->CharaView.PortraitCharacterData.ItemIds[i]);

        return new PlateSnapshot
        {
            Name = data->Name.ToString(),
            WorldId = data->WorldId,
            ClassJobId = data->ClassJobId,
            Level = data->Level,
            TitleId = data->TitleId,
            FreeCompany = data->FreeCompany.ToString(),
            SearchComment = data->SearchComment.ToString(),
            BasePlate = data->PlateDesign.BasePlate,
            TopBorder = data->PlateDesign.TopBorder,
            BottomBorder = data->PlateDesign.BottomBorder,
            InvertPortraitPlacement = data->InvertPortraitPlacement,
            Decorations = decorations,
            Gear = gear,
            GearClassJobId = data->CharaView.PortraitCharacterData.ClassJobId,
            Portrait = new PortraitSnapshot
            {
                CameraPositionX = portrait.CameraPosition.X,
                CameraPositionY = portrait.CameraPosition.Y,
                CameraPositionZ = portrait.CameraPosition.Z,
                CameraPositionW = portrait.CameraPosition.W,
                CameraTargetX = portrait.CameraTarget.X,
                CameraTargetY = portrait.CameraTarget.Y,
                CameraTargetZ = portrait.CameraTarget.Z,
                CameraTargetW = portrait.CameraTarget.W,
                ImageRotation = portrait.ImageRotation,
                CameraZoom = portrait.CameraZoom,
                BannerTimeline = portrait.BannerTimeline,
                AnimationProgress = portrait.AnimationProgress,
                Expression = portrait.Expression,
                BannerBg = portrait.BannerBg,
                BannerFrame = data->BannerFrame,
                BannerDecoration = data->BannerDecoration,
                DirectionalLightingColorRed = portrait.DirectionalLightingColorRed,
                DirectionalLightingColorGreen = portrait.DirectionalLightingColorGreen,
                DirectionalLightingColorBlue = portrait.DirectionalLightingColorBlue,
                DirectionalLightingBrightness = portrait.DirectionalLightingBrightness,
                DirectionalLightingVerticalAngle = portrait.DirectionalLightingVerticalAngle,
                DirectionalLightingHorizontalAngle = portrait.DirectionalLightingHorizontalAngle,
                AmbientLightingColorRed = portrait.AmbientLightingColorRed,
                AmbientLightingColorGreen = portrait.AmbientLightingColorGreen,
                AmbientLightingColorBlue = portrait.AmbientLightingColorBlue,
                AmbientLightingBrightness = portrait.AmbientLightingBrightness,
            },
            IsSelf = local != null && local->ContentId == data->ContentId,
            WasResetDueToFantasia = data->WasResetDueToFantasia,
            ContentId = data->ContentId,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Switch()
    {
        if (latest == null || stashed == null || latest.ContentId == stashed.ContentId)
            return;
        (latest, stashed) = (stashed, latest);
        window.Refresh(latest, stashed);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnPlateEvent);
        Plugin.AddonLifecycle.UnregisterListener(OnPlateClosed);
        Plugin.AddonLifecycle.UnregisterListener(OnUnlockEditorOpened);
        Plugin.PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
    }

    internal sealed class PlateSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public ushort WorldId { get; init; }
        public byte ClassJobId { get; init; }
        public ushort Level { get; init; }
        public ushort TitleId { get; init; }
        public string FreeCompany { get; init; } = string.Empty;
        public string SearchComment { get; init; } = string.Empty;
        public ushort BasePlate { get; init; }
        public byte TopBorder { get; init; }
        public byte BottomBorder { get; init; }
        public bool InvertPortraitPlacement { get; init; }
        public IReadOnlyList<ushort> Decorations { get; init; } = [];
        public IReadOnlyList<uint> Gear { get; init; } = [];
        public byte GearClassJobId { get; init; }
        public PortraitSnapshot Portrait { get; init; } = new();
        public bool IsSelf { get; init; }
        public bool WasResetDueToFantasia { get; init; }
        public ulong ContentId { get; init; }
        public DateTimeOffset CapturedAt { get; init; }
    }

    internal sealed class PortraitSnapshot
    {
        public Half CameraPositionX { get; init; }
        public Half CameraPositionY { get; init; }
        public Half CameraPositionZ { get; init; }
        public Half CameraPositionW { get; init; }
        public Half CameraTargetX { get; init; }
        public Half CameraTargetY { get; init; }
        public Half CameraTargetZ { get; init; }
        public Half CameraTargetW { get; init; }
        public short ImageRotation { get; init; }
        public byte CameraZoom { get; init; }
        public ushort BannerTimeline { get; init; }
        public float AnimationProgress { get; init; }
        public byte Expression { get; init; }
        public ushort BannerBg { get; init; }
        public ushort BannerFrame { get; init; }
        public ushort BannerDecoration { get; init; }
        public byte DirectionalLightingColorRed { get; init; }
        public byte DirectionalLightingColorGreen { get; init; }
        public byte DirectionalLightingColorBlue { get; init; }
        public byte DirectionalLightingBrightness { get; init; }
        public short DirectionalLightingVerticalAngle { get; init; }
        public short DirectionalLightingHorizontalAngle { get; init; }
        public byte AmbientLightingColorRed { get; init; }
        public byte AmbientLightingColorGreen { get; init; }
        public byte AmbientLightingColorBlue { get; init; }
        public byte AmbientLightingBrightness { get; init; }
    }

    private sealed class AdventurerPlateWindow : Window
    {
        private readonly AdventurerPlateFeature owner;
        private PlateSnapshot? current;
        private PlateSnapshot? pending;

        public AdventurerPlateWindow(AdventurerPlateFeature owner) : base("冒险者铭牌###KeitaToolboxPlate")
        {
            this.owner = owner;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new System.Numerics.Vector2(420, 260) };
        }

        public void Refresh(PlateSnapshot? snapshot, PlateSnapshot? pending)
        {
            current = snapshot;
            this.pending = pending;
        }

        public override void Draw()
        {
            if (current == null)
            {
                ImGui.TextUnformatted("尚未捕获铭牌，请打开任意角色的冒险者铭牌。");
                return;
            }
            ImGui.TextUnformatted($"{current.Name}  Lv{current.Level}  World#{current.WorldId}");
            ImGui.SameLine();
            var pinned = Plugin.Config.AdventurerPlate.Pinned;
            if (ImGui.Checkbox("固定", ref pinned))
            {
                Plugin.Config.AdventurerPlate.Pinned = pinned;
                Plugin.Config.Save();
            }
            if (pending != null && pending.ContentId != current.ContentId &&
                ImGui.SmallButton($"切换到 {pending.Name}"))
                owner.Switch();
            if (current.WasResetDueToFantasia)
                ImGui.TextColored(new System.Numerics.Vector4(1, .7f, .2f, 1), "该铭牌因幻想药被重置。");
            if (ImGui.BeginTabBar("###KeitaToolboxPlateTabs"))
            {
                if (ImGui.BeginTabItem("设计"))
                {
                    DrawNamedEntry("铭牌底色", current.BasePlate, "base");
                    DrawNamedEntry("铭牌顶部装饰", current.TopBorder, "top");
                    DrawNamedEntry("铭牌底部装饰", current.BottomBorder, "bottom");
                    foreach (var id in current.Decorations)
                        DrawNamedEntry(DesignCategory(id), id, "deco");
                    ImGui.TextUnformatted($"肖像反转：{(current.InvertPortraitPlacement ? "是" : "否")}");
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("肖像"))
                {
                    var portraitJob = Plugin.Data.GetExcelSheet<ClassJob>().TryGetRow(current.GearClassJobId, out var job)
                        ? job.Name.ExtractText()
                        : $"#{current.GearClassJobId}";
                    ImGui.TextUnformatted($"肖像职业：{portraitJob}");
                    DrawNamedEntry("背景", current.Portrait.BannerBg, "bg");
                    DrawNamedEntry("边框", current.Portrait.BannerFrame, "frame");
                    DrawNamedEntry("装饰", current.Portrait.BannerDecoration, "deco");
                    DrawNamedEntry("姿势", current.Portrait.BannerTimeline, "pose");
                    DrawNamedEntry("表情", current.Portrait.Expression, "expression");
                    ImGui.TextUnformatted($"缩放：{current.Portrait.CameraZoom}  旋转：{current.Portrait.ImageRotation}");
                    ImGui.TextUnformatted($"环境光：RGB({current.Portrait.AmbientLightingColorRed}, {current.Portrait.AmbientLightingColorGreen}, {current.Portrait.AmbientLightingColorBlue}) 强度 {current.Portrait.AmbientLightingBrightness}");
                    ImGui.TextUnformatted($"方向性灯光：RGB({current.Portrait.DirectionalLightingColorRed}, {current.Portrait.DirectionalLightingColorGreen}, {current.Portrait.DirectionalLightingColorBlue}) 强度 {current.Portrait.DirectionalLightingBrightness}，垂直 {current.Portrait.DirectionalLightingVerticalAngle}°，水平 {current.Portrait.DirectionalLightingHorizontalAngle}°");
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("资料"))
                {
                    if (current.FreeCompany.Length > 0) ImGui.TextUnformatted($"部队：{current.FreeCompany}");
                    if (current.SearchComment.Length > 0) ImGui.TextWrapped(current.SearchComment);
                    ImGui.TextUnformatted("肖像装备：");
                    foreach (var itemId in current.Gear)
                    {
                        if (Plugin.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
                        {
                            var itemName = $"{item.Name.ExtractText()} ({itemId})";
                            ImGui.TextUnformatted($"  {itemName}");
                        }
                        else
                            ImGui.TextUnformatted($"  未知物品 ({itemId}) [未知]");
                    }
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            if (ImGui.Button("复制肖像预设"))
            {
                ImGui.SetClipboardText(EncodePreset(current.Portrait));
                Plugin.Chat.Print("[Keita 工具箱] 肖像预设已复制到剪贴板。");
            }
            ImGui.SameLine();
            if (ImGui.Button("复制铭牌文本"))
            {
                ImGui.SetClipboardText(FormatText(current));
                Plugin.Chat.Print("[Keita 工具箱] 铭牌文本已复制到剪贴板。");
            }
        }

        private void DrawNamedEntry(string label, ushort id, string kind)
        {
            var (name, _) = ResolveName(id, kind);
            ImGui.TextUnformatted($"{label}：{name}");
        }

        private (string Name, bool? Unlocked) ResolveName(ushort id, string kind)
        {
            if (id == 0)
                return ("无 (0)", true);
            var name = string.Empty;
            bool? unlocked = null;
            switch (kind)
            {
                case "base" when Plugin.Data.GetExcelSheet<CharaCardBase>().TryGetRow(id, out var baseRow):
                    name = baseRow.Name.ExtractText(); unlocked = IsConditionUnlocked(baseRow.UnlockCondition.RowId); break;
                case "top" or "bottom" when Plugin.Data.GetExcelSheet<CharaCardHeader>().TryGetRow(id, out var headerRow):
                    name = headerRow.Name.ExtractText(); unlocked = IsConditionUnlocked(headerRow.UnlockCondition.RowId); break;
                case "deco" when Plugin.Data.GetExcelSheet<CharaCardDecoration>().TryGetRow(id, out var decoRow):
                    name = decoRow.Name.ExtractText(); unlocked = IsConditionUnlocked(decoRow.UnlockCondition.RowId); break;
                case "bg" when Plugin.Data.GetExcelSheet<BannerBg>().TryGetRow(id, out var bgRow):
                    name = bgRow.Name.ExtractText(); unlocked = IsConditionUnlocked(bgRow.UnlockCondition.RowId); break;
                case "frame" when Plugin.Data.GetExcelSheet<BannerFrame>().TryGetRow(id, out var frameRow):
                    name = frameRow.Name.ExtractText(); unlocked = IsConditionUnlocked(frameRow.UnlockCondition.RowId); break;
                case "expression" when Plugin.Data.GetExcelSheet<BannerFacial>().TryGetRow(id, out var facialRow):
                    name = facialRow.Emote.Value.Name.ExtractText(); unlocked = IsConditionUnlocked(facialRow.UnlockCondition.RowId); break;
                case "pose" when Plugin.Data.GetExcelSheet<BannerTimeline>().TryGetRow(id, out var timelineRow):
                    name = timelineRow.Name.ExtractText(); unlocked = IsConditionUnlocked(timelineRow.UnlockCondition.RowId); break;
                case "deco" when Plugin.Data.GetExcelSheet<BannerDecoration>().TryGetRow(id, out var bannerDecoRow):
                    name = bannerDecoRow.Name.ExtractText(); unlocked = IsConditionUnlocked(bannerDecoRow.UnlockCondition.RowId); break;
            }
            return (string.IsNullOrWhiteSpace(name) ? $"未知 ({id})" : $"{name} ({id})", unlocked);
        }

        private static string DesignCategory(ushort id)
        {
            if (!Plugin.Data.GetExcelSheet<CharaCardDecoration>().TryGetRow(id, out var row))
                return "铭牌装饰";
            return row.Component switch
            {
                1 => "铭牌背衬",
                2 => "铭牌花纹",
                3 => "肖像外框",
                4 => "铭牌外框",
                5 => "铭牌装饰",
                _ => "铭牌装饰",
            };
        }

        private bool? IsConditionUnlocked(uint conditionId)
        {
            if (conditionId == 0)
                return true;
            if (owner.unlockCache.TryGetValue(conditionId, out var cached))
                return cached;
            if (!Plugin.Data.GetExcelSheet<BannerCondition>().TryGetRow(conditionId, out var condition))
                return null;
            if (condition.UnlockType1 == 0 && condition.UnlockType2 == 0)
                return true;
            return Plugin.UnlockState.IsBannerConditionUnlocked(condition);
        }

        private static string FormatText(PlateSnapshot s) =>
            $"Adventurer Plate: {s.Name} @ World#{s.WorldId}\n" +
            $"Level: {s.Level}  Job: {s.ClassJobId}\n" +
            $"Base plate: {s.BasePlate}\nPortrait: bg={s.Portrait.BannerBg}, frame={s.Portrait.BannerFrame}, pose={s.Portrait.BannerTimeline}\n" +
            $"Gear IDs: {string.Join(", ", s.Gear)}";

        private static string EncodePreset(PortraitSnapshot p)
        {
            using var stream = new MemoryStream(64);
            using var writer = new BinaryWriter(stream);
            writer.Write(0x53505448); writer.Write((ushort)1);
            writer.Write(p.CameraPositionX); writer.Write(p.CameraPositionY); writer.Write(p.CameraPositionZ); writer.Write(p.CameraPositionW);
            writer.Write(p.CameraTargetX); writer.Write(p.CameraTargetY); writer.Write(p.CameraTargetZ); writer.Write(p.CameraTargetW);
            writer.Write(p.ImageRotation); writer.Write(p.CameraZoom); writer.Write(p.BannerTimeline); writer.Write(p.AnimationProgress); writer.Write(p.Expression);
            writer.Write((Half)0); writer.Write((Half)0); writer.Write((Half)0); writer.Write((Half)0);
            writer.Write((byte)255); writer.Write((byte)255); writer.Write((byte)255); writer.Write((byte)100); writer.Write((short)0); writer.Write((short)0);
            writer.Write((byte)255); writer.Write((byte)255); writer.Write((byte)255); writer.Write((byte)100);
            writer.Write(p.BannerBg); writer.Write(p.BannerFrame); writer.Write(p.BannerDecoration);
            return Convert.ToBase64String(stream.ToArray());
        }
    }
}
