﻿using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using HarmonyLib.Tools;
using UnityEngine;
using UnityEngine.UI;

/*
TODO:
    - Get rid of 2nd prompt from Guadromon conversation to remove button presses (make it like Gutsumon/Tyrannomon)
        - Can skip 2nd prompt entirely by running different scriptCommand.
        - Find code responsible for displaying them item acquired. Would be really nice to simply not run any script and display that item acquisition panel (w/item icon and count)
            - looks to be the product of TalkMain.CommonMessageWindow but it looks like it has a specific setup, takes specific arguments, and removes it's own references when finished
            - Can we implement our own version of TalkMain.CommonMessageWindow that will force a window with custom text/image to open when called
            - Partial success!!! Was able to make my own function that made a uCommonMessageWindow appear with Japanese error text. Got to see what in the setup I'm doing wrong PICK UP HERE
    - Look into generating new panel to the right of screen kind of like a shop
    - Include thumbstick for +/- num_exchange (currently on DPad and arrow keys)
    - Include keyboard equivalent of gamepad Square for maxing num_exchange

I think all exchanges can be handled via csvbId + blockId format. Mapping them here: [csvbId = Scenario04] for all
    - Gaurdromon    D034_*
        - initial option selection is Scenario04/D034_MENU0X where confirmation is SubScenario/D034_MENUX0
    - Gotsumon      C024_*
    - Tyrannomon    D021_*

[C024]
MaterialChange01 = Gostumon (metal)
MaterialChange02 = Gostumon (stone)
TreasureMaterial = Gostumon (special)

[D021]
MaterialChange03 = Tyrannomon (wood)
AdventureInfo = Tyrannomon (liquid)
MaterialChange04 = Tyrannomon (special)

[D034]
window_type _03 = Gaurdromon (lab item creation)
 - Since there's an in-between dialog option, cut this out and replace the confirmation (are you sure?) with a confirmation (exchange complete)
 - Front-load item requirements to the menu by red-lining the options and disabling them if required item's aren't present
*/

namespace ConversionOverhaul;

public class SelectedItem
{
    public SelectedItem(ItemType type, uint id) {
        this.item_type = type;
        this.item_name = Language.GetString(id);
        this.item_id = id;
    }

    public int num_exchanges = 1;
    public ItemType item_type;
    public uint item_id;
    public string item_name;
    public int item_count { get { 
        if (this.item_type == ItemType.Material) {
            return Plugin.town_materials[item_name];
        }
        if (this.item_type == ItemType.Item) {
            return Plugin.player_items[item_name];
        }
        return 0;
    } }

    public enum ItemType
	{
        Material,
        Item
    }
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public static ManualLogSource Logger;
    public static Type CScenarioScript;

    public static List<ParameterCommonSelectWindowMode.WindowType> sunday_trade_window_types = new List<ParameterCommonSelectWindowMode.WindowType> {
        ParameterCommonSelectWindowMode.WindowType.MaterialChange04,
        ParameterCommonSelectWindowMode.WindowType.TreasureMaterial
    };
    public static List<ParameterCommonSelectWindowMode.WindowType> town_material_window_types = new List<ParameterCommonSelectWindowMode.WindowType> {
        ParameterCommonSelectWindowMode.WindowType.MaterialChange01,
        ParameterCommonSelectWindowMode.WindowType.MaterialChange02,
        ParameterCommonSelectWindowMode.WindowType.MaterialChange03,
        ParameterCommonSelectWindowMode.WindowType.AdventureInfo,
    }.Concat(sunday_trade_window_types).ToList();
    public static List<ParameterCommonSelectWindowMode.WindowType> player_inventory_window_types = new List<ParameterCommonSelectWindowMode.WindowType> {
        ParameterCommonSelectWindowMode.WindowType._03
    };

    public static string[] town_material_script_types = new [] { "C024", "D021" };
    public static string[] player_inventory_script_types = new [] { "D034" };
    public static string[] script_types_we_care_about = town_material_script_types.Concat(player_inventory_script_types).ToArray();

    public static List<ParameterCommonSelectWindowMode.WindowType> windows_we_care_about = Plugin.town_material_window_types.Concat(Plugin.player_inventory_window_types).ToList();
    public static Dictionary<string, int> town_materials = new Dictionary<string, int>();
    public static Dictionary<string, int> player_items = new Dictionary<string, int>();
    public static List<(string, int)> selected_recipe = new List<(string, int)>();
    public static int selected_option;
    public static int num_exchanges;
    public static MethodInfo original_CallCmdBlockCommonSelectWindow;

    public static (string, (string, int)[])[] lab_recipes = new [] {
        ( "Double Disk", new [] { ("MP Disk", 2), ("Recovery Disk", 2) } ),
        ( "Large Double Disk", new [] { ("Medium MP Disk", 3), ("Medium Recovery Disk", 3) } ),
        ( "Super Double Disk", new [] { ("Large MP Disk", 2), ("Large Recovery Disk", 2) } ),
        ( "Large Recovery Disk", new [] { ("Medium Recovery Disk", 5) } ),
        ( "Large MP Disk", new [] { ("Medium MP Disk", 5) } ),
        ( "Full Remedy Disk", new [] { ("Remedy Disk", 10) } ),
        ( "Super Regen Disk", new [] { ("Regen Disk", 10) } ),
        ( "Medicine", new [] { ("Bandage", 5), ("Recovery Disk", 3) } )
    };

    public static Dictionary<string, ParameterItemData> ITEM_LOOKUP = new Dictionary<string, ParameterItemData>();

    public static MethodInfo GetOriginalMethod(string className, string methodName)
    {
        return AccessTools.Method(AccessTools.TypeByName(className), methodName);
    }

    public override void Load()
    {
        Plugin.Logger = base.Log;
        HarmonyFileLog.Enabled = true;

        // Plugin startup logic
        Plugin.Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        this.Awake();
    }

    public void Awake()
    {
        Harmony harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Plugin.original_CallCmdBlockCommonSelectWindow = harmony.Patch(GetOriginalMethod("CScenarioScript", "CallCmdBlockCommonSelectWindow"));
        harmony.PatchAll();
    }

    public static uCommonMessageWindow MessageWindowWithImage(string message, uint itemID = 0)
	{
        uCommonMessageWindow message_window = UnityEngine.Object.Instantiate<uCommonMessageWindow>(MainGameManager.Ref.MessageManager.Get00()).GetComponent<uCommonMessageWindow>();
        message_window.Initialize(0);
        
        GameObject iconObject = new GameObject("Image");
        iconObject.hideFlags = HideFlags.DontSave;
        iconObject.transform.SetParent(message_window.transform.Find("Root").Find("Anim"), false);
        Image image = iconObject.AddComponent<Image>();
        image.enabled = false;
        iconObject.SetActive(false);

        ScreenEffectScript.Ref.ToColorBegin(new Color32(0, 0, 0, 180), 0.5f, null, null);
        message_window.SetLangMessage(message, uCommonMessageWindow.Pos.Center);
        message_window.enablePanel(true, false);
        RectTransform baseRect = image.transform.parent.Find("Base").GetComponent<RectTransform>();
        Text text = image.transform.parent.Find("Text").GetComponent<Text>();

        Sprite sprite = null;
        if (itemID > 0) {
            ParameterItemData item = Plugin.ITEM_LOOKUP[Language.GetString(itemID)];
            sprite = uItemBase.LoadIconImage(ref item);

            image.sprite = sprite;
            image.gameObject.SetActive(true);
            image.gameObject.GetComponent<RectTransform>().sizeDelta = new Vector2((float)sprite.texture.width, (float)sprite.texture.height);
            image.gameObject.transform.localScale = Vector3.zero;
            TweenScale.Begin(image.gameObject, 0.1f, Vector3.one);
            image.transform.localPosition = new Vector3(0f, baseRect.sizeDelta.y / 2f + (float)sprite.texture.height / 2f, 0f);
            image.enabled = true;
        }
        else {
            image.gameObject.SetActive(false);
        }
        message_window.UpdateMain();
        
        return message_window;

		// if (this.m_common_message_window == null)
		// {
		// 	this.m_common_message_window = UnityEngine.Object.Instantiate<uCommonMessageWindow>(MainGameManager.Ref.MessageManager.Get00()).GetComponent<uCommonMessageWindow>();
		// 	yield return null;
		// 	this.m_common_message_window.Initialize(0);
		// 	yield return null;
		// 	GameObject iconObject = new GameObject("Image");
		// 	iconObject.hideFlags = HideFlags.DontSave;
		// 	iconObject.transform.SetParent(this.m_common_message_window.transform.FindChild("Root").FindChild("Anim"), false);
		// 	if (this.m_iconImage == null)
		// 	{
		// 		this.m_iconImage = iconObject.AddComponent<Image>();
		// 		this.m_iconImage.enabled = false;
		// 	}
		// 	iconObject.SetActive(false);
		// 	yield return null;
		// }
		// if (this.m_common_message_window != null)
		// {
		// 	ScreenEffectScript.Ref.ToColorBegin(new Color32(0, 0, 0, 180), 0.5f, null, null);
		// 	string message = Language.GetStringWithButtonIcon(arg0);
		// 	this.m_common_message_window.SetLangMessage(message, uCommonMessageWindow.Pos.Center);
		// 	this.m_common_message_window.enablePanel(true, false);
		// 	RectTransform baseRect = this.m_iconImage.transform.parent.FindChild("Base").GetComponent<RectTransform>();
		// 	Text text = this.m_iconImage.transform.parent.FindChild("Text").GetComponent<Text>();
		// 	base.SetText(text, message);
		// 	baseRect.sizeDelta = new Vector2(text.rectTransform.sizeDelta.x * 0.5f + 112f, text.rectTransform.sizeDelta.y + 20f);
		// 	text.text = message;
		// 	Sprite sprite = null;
		// 	if (arg1 != null && arg1.Length > 0)
		// 	{
		// 		string resouce_name = "UI/item_icon" + arg1.Replace("ui/itemicons", string.Empty);
		// 		ResourceRequest request = Resources.LoadAsync<Sprite>(resouce_name);
		// 		while (!request.isDone)
		// 		{
		// 			yield return null;
		// 		}
		// 		if (request.asset != null)
		// 		{
		// 			sprite = (request.asset as Sprite);
		// 		}
		// 	}
		// 	if (sprite != null)
		// 	{
		// 		if (this.m_iconImage.sprite)
		// 		{
		// 			this.m_iconImage.sprite = null;
		// 		}
		// 		this.m_iconImage.sprite = sprite;
		// 		this.m_iconImage.gameObject.SetActive(true);
		// 		this.m_iconImage.gameObject.GetComponent<RectTransform>().sizeDelta = new Vector2((float)sprite.texture.width, (float)sprite.texture.height);
		// 		this.m_iconImage.gameObject.transform.localScale = Vector3.zero;
		// 		TweenScale.Begin(this.m_iconImage.gameObject, 0.1f, Vector3.one);
		// 		this.m_iconImage.transform.localPosition = new Vector3(0f, baseRect.sizeDelta.y / 2f + (float)sprite.texture.height / 2f, 0f);
		// 		this.m_iconImage.enabled = true;
		// 	}
		// 	else
		// 	{
		// 		this.m_iconImage.gameObject.SetActive(false);
		// 	}
		// 	do
		// 	{
		// 		yield return null;
		// 		this.m_common_message_window.UpdateMain();
		// 	}
		// 	while (this.m_common_message_window.IsOpened());
		// 	if (this.m_iconImage.gameObject.activeInHierarchy)
		// 	{
		// 		TweenScale.Begin(this.m_iconImage.gameObject, 0.1f, Vector3.zero);
		// 	}
		// 	if (this.m_iconImage.sprite != null)
		// 	{
		// 		this.m_iconImage.sprite = null;
		// 		this.m_iconImage.enabled = false;
		// 	}
		// 	ScreenEffectScript.Ref.FadeInBegin(0.5f, null, null);
		// }
		// yield break;
	}

}

[HarmonyPatch(typeof(AppMainScript), "_FinishedParameterLoad")]
public static class Patch_AppMainScript__FinishedParameterLoad
{
    public static void Postfix()
    {
        Plugin.ITEM_LOOKUP.Clear();
        foreach (var item in AppMainScript.Ref.m_parameters.m_csvbItemData.m_params) {
            Plugin.ITEM_LOOKUP[Language.GetString(item.id)] = item;
            // Plugin.Logger.LogInfo($"{Language.GetString(item.m_id)}\t{item.m_id}");
        }
    }
}

[HarmonyPatch(typeof(uCommonSelectWindowPanel), "Setup")]
[HarmonyPatch(new Type[] { typeof(ParameterCommonSelectWindowMode.WindowType) }, new ArgumentType[] { ArgumentType.Ref } )]
public static class Patch_uCommonSelectWindowPanel_Setup
{
    public static void Postfix(ParameterCommonSelectWindowMode.WindowType window_type, uCommonSelectWindowPanel __instance) {
        Plugin.selected_recipe.Clear();
        Plugin.selected_option = -1;

        if (Plugin.town_material_window_types.Contains(window_type)) {
            Plugin.town_materials.Clear();
            foreach (var material in StorageData.m_materialData.m_materialDatas) {
                string key = Language.GetString(material.m_id);
                if (!string.IsNullOrEmpty(key))
                    Plugin.town_materials[key] = material.m_material_num;
            }
        }

        if (Plugin.player_inventory_window_types.Contains(window_type)) {
            dynamic itemList = StorageData.m_ItemStorageData.m_itemDataListTbl[(int)ItemStorageData.StorageType.PLAYER].ToArray();
            
            Plugin.player_items.Clear();
            foreach (var item in itemList) {
                string key = Language.GetString(item.m_itemID);
                if (!string.IsNullOrEmpty(key))
                    Plugin.player_items[key] = item.m_itemNum;
            }
        }

        if (Plugin.player_inventory_window_types.Contains(window_type)) {
            for (int i = 0; i < __instance.m_paramCommonSelectWindowList.Count; i++){
                (string, (string name , int count)[] items) recipe = Plugin.lab_recipes[i];
                var windowOptionParams = __instance.m_paramCommonSelectWindowList[i];

                windowOptionParams.m_select_mode1 = 8;
                windowOptionParams.m_select_format1 = 1;
                windowOptionParams.m_select_item1 = Plugin.ITEM_LOOKUP[recipe.items[0].name].m_id;
                windowOptionParams.m_select_value1 = recipe.items[0].count;
                if (recipe.items.Count() > 1) {
                    windowOptionParams.m_select_mode2 = 8;
                    windowOptionParams.m_select_format2 = 1;
                    windowOptionParams.m_select_item2 = Plugin.ITEM_LOOKUP[recipe.items[1].name].m_id;
                    windowOptionParams.m_select_value2 = recipe.items[1].count;
                }
            }
        }

        uCommonMessageWindow message_window = Plugin.MessageWindowWithImage("This is a Meat!", Plugin.ITEM_LOOKUP["Meat"].m_id);
        Plugin.Logger.LogInfo($"message_window {message_window}");
    }
}

[HarmonyPatch(typeof(uCommonSelectWindowPanel), "Update")]
public static class Patch_uCommonSelectWindowPanel_Update
{
    public static void SetCaptionText(uCommonSelectWindowPanelCaption captionPanel, string replace, string with) {
        Text captionText = captionPanel.m_text;
        UtilityScript.SetLangButtonText(ref captionText, "cw_caption_0");
        captionText.text = captionText.text.Replace(replace, with);
    }

    public static void Postfix(uCommonSelectWindowPanel __instance) {
        dynamic window_type = __instance.m_windowType;
        if (!Plugin.windows_we_care_about.Contains(window_type))
            return;

        int selected_option = __instance.m_itemPanel.m_selectNo;
        // Weird special-case during Sunday trades, the window_list has an extra element in the middle of the list throwing off the index
        // Temp-solution: +1 to "selected_option" so we can pull the correct item
        if (Plugin.town_material_window_types.Contains(window_type) && selected_option > 2)
            selected_option += 1;

        if (Plugin.selected_recipe.Any()) {
            var itemCollection = Plugin.player_inventory_window_types.Contains(window_type) ? Plugin.player_items : Plugin.town_materials;
            int max_num_exchanges = Plugin.selected_recipe.Select(x => itemCollection[(string)x.Item1] / (int)x.Item2).Min();;
            int num_exchanges = Plugin.num_exchanges;
            if (PadManager.IsTrigger(PadManager.BUTTON.bSquare))
                num_exchanges = max_num_exchanges;
            if (PadManager.IsRepeat(PadManager.BUTTON.dLeft)) 
                num_exchanges--;
            if (PadManager.IsRepeat(PadManager.BUTTON.dRight))
                num_exchanges++;
            if (num_exchanges > max_num_exchanges)
                num_exchanges = max_num_exchanges;
            if (num_exchanges < 1)
                num_exchanges = 1;
            if (num_exchanges != Plugin.num_exchanges)
                CriSoundManager.PlayCommonSe("S_005");
            Plugin.num_exchanges = num_exchanges;
            SetCaptionText(__instance.m_captionPanel, "OK", $"Exchange x{num_exchanges}");
        }

        if (selected_option == Plugin.selected_option)
            return;

        Plugin.selected_option = selected_option;
        Plugin.num_exchanges = 1;
        //Plugin.Logger.LogInfo($"selected_option {Plugin.selected_option}");

        dynamic windowOptionParams = __instance.m_paramCommonSelectWindowList[selected_option];
        //Plugin.Logger.LogInfo($"window {window}");
        string scriptCommand = windowOptionParams.m_scriptCommand;
        //Plugin.Logger.LogInfo($"scriptCommand {scriptCommand}");
        string scriptType = scriptCommand.Split("_")[0];
        //Plugin.Logger.LogInfo($"scriptType {scriptType}");
        
        Plugin.selected_recipe.Clear();
        //Plugin.Logger.LogInfo($"selected_recipe {Plugin.selected_recipe}");
        //Plugin.Logger.LogInfo($"window_type {window_type}");
        if (Plugin.town_material_window_types.Contains(window_type)) {
            uint item_id = windowOptionParams.m_select_item1;
            //Plugin.Logger.LogInfo($"item_id {item_id}");
            Plugin.selected_recipe.Add((Language.GetString(item_id), windowOptionParams.m_select_value1));
            //Plugin.Logger.LogInfo($"selected_recipe {Plugin.selected_recipe}");
        }
        if (Plugin.player_inventory_window_types.Contains(window_type)) {
            //Plugin.Logger.LogInfo($"TEST {window.m_select_item1}");
            (string, (string, int)[] Input) recipe = Plugin.lab_recipes[selected_option];
            //Plugin.Logger.LogInfo($"recipe {recipe}");
            foreach ((string name, int count) item in recipe.Input) {
                Plugin.selected_recipe.Add((item.name, item.count));
            }
            //Plugin.Logger.LogInfo($"selected_recipe {Plugin.selected_recipe}");
        }
    }
}

[HarmonyPatch]
public static class Patch_CScenarioScriptBase_CallCsvbBlock
{
    [HarmonyTargetMethod]
    public static MethodBase TargetMethod(Harmony instance) {
        return Plugin.GetOriginalMethod("CScenarioScriptBase", "CallCsvbBlock");
    }

    public static void Postfix(string _csvbId, string _blockId, dynamic __instance) {
        Plugin.Logger.LogInfo($"_csvbId {_csvbId} _blockId {_blockId}");
        
        TalkMain talkMain = MainGameManager.Ref.eventScene;
        Plugin.Logger.LogInfo($"talkMain {talkMain}");
        Plugin.Logger.LogInfo($"talkMain window {talkMain.m_common_message_window}");

        string item_name = "Meat";
        string arg0 = "TOWN_TALK_D034_026";
        string arg1 = $"ui/itemicons/{Plugin.ITEM_LOOKUP[item_name].m_iconName}";
        talkMain.CommonMessageWindow(arg0, arg1, "", "", "", "");
        Plugin.Logger.LogInfo($"talkMain window {talkMain.m_common_message_window}");
    }
}

[HarmonyPatch]
public static class Patch_CScenarioScriptBase_CallAllCsvbBlock
{
    [HarmonyTargetMethod]
    public static MethodBase TargetMethod(Harmony instance) {
        return Plugin.GetOriginalMethod("CScenarioScriptBase", "CallAllCsvbBlock");
    }

    public static void Postfix(string _blockId, dynamic __instance) {
        Plugin.Logger.LogInfo($"_blockId {_blockId}");
    }
}


// [HarmonyPatch]
// public static class Patch_CScenarioScript_CallCmdBlockCommonSelectWindow
// {
//     [HarmonyTargetMethod]
//     public static MethodBase TargetMethod(Harmony instance) {
//         return Plugin.GetOriginalMethod("CScenarioScript", "CallCmdBlockCommonSelectWindow");
//     }

//     public static bool Prefix(ParameterCommonSelectWindow _param, dynamic __instance) {
//         string scriptCommand = _param.m_scriptCommand;
//         string scriptType = scriptCommand.Split("_")[0];
//         if (scriptType == "D034") {
//             string scriptID = scriptCommand.Substring(scriptCommand.Length - 2);
//             scriptCommand = scriptCommand.Replace(scriptID, (Int32.Parse(scriptID) * 10).ToString());
//         }

//         Plugin.Logger.LogInfo($"Calling {scriptCommand} x{Plugin.num_exchanges} times");
//         for (int i = 0; i < Plugin.num_exchanges; i++) {
//             __instance.CallCmdBlockChapter(scriptCommand);
//             // Plugin.original_CallCmdBlockCommonSelectWindow.Invoke(__instance, new object[] { __instance, _param });
//         }

//         return false;
//     }
// }

// [HarmonyPatch(typeof(uItemBase), "SetItemContent")]
// [HarmonyPatch(new Type[] { typeof(uItemParts), typeof(ItemData), typeof(ParameterItemData) }, new ArgumentType[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref } )]
// public static class Patch_UtilityScript_IsListActiveRef
// {
//     public static void Postfix(uItemParts item, ItemData item_data, ParameterItemData param_item_data, dynamic __instance) {
//         if (item_data.GetState() != ItemData.State.ParamCommonSelectWindow)
//             return;

//         string isAvailable = __instance.UnavailableItem(ref item_data, ref param_item_data) ? "Unavailable" : "Available";
//         Plugin.Logger.LogInfo($"[{item_data.GetState()}] [{isAvailable}] \"{item.m_name.text}\"");

//         var param_common = item_data.m_paramCommonSelectWindowData;
//         Plugin.Logger.LogInfo($"m_select_1 {param_common.m_select_mode1} {param_common.m_select_format1} {param_common.m_select_value1} {param_common.m_select_item1} {param_common.m_select_digimon1}");
//         Plugin.Logger.LogInfo($"m_select_2 {param_common.m_select_mode2} {param_common.m_select_format2} {param_common.m_select_value2} {param_common.m_select_item2} {param_common.m_select_digimon2}");
//         Plugin.Logger.LogInfo($"");
//     }
// }

[HarmonyPatch]
public static class Patch_ParameterCommonSelectWindow_IsSelectModeActive
{
    public static MethodBase TargetMethod(Harmony instance) {
        return Plugin.GetOriginalMethod("ParameterCommonSelectWindow", "IsSelectModeActive");
    }

    public static bool isPlayerHaveItem(ParameterCommonSelectWindow.CheckData checkData) {
        return Plugin.player_items[Language.GetString(checkData.m_item)] >= checkData.m_value;
    }

    public static void Postfix(ref dynamic __result, ParameterCommonSelectWindow __instance) {
        dynamic checkDataList = __instance.GetCheckDataList(ParameterCommonSelectWindow.CheckDataType.SelectData).ToArray();

        foreach (var checkData in checkDataList) {
            if (checkData.m_mode == 8) {
                __result = isPlayerHaveItem(checkData);
            }
            if (!__result){
                break;
            }
        }
    }
}

// [HarmonyPatch(typeof(TalkMain), "CommonMessageWindow")]
// public static class Test3
// {
//     public static void Prefix(string arg0, ref string arg1, dynamic __instance)
//     {
//         Plugin.Logger.LogInfo($"TalkMain::CommonMessageWindow:Prefix");
//         string item_name = "Meat";
//         arg1 = $"ui/itemicons/{Plugin.ITEM_LOOKUP[item_name].m_iconName}";
        
//         // arg0 = "TOWN_TALK_D034_026";
//         // arg1 = $"ui/itemicons/{Plugin.ITEM_LOOKUP[item_name].m_iconName}";
//         // Plugin.Logger.LogInfo($"arg0 {arg0}");
//         // Plugin.Logger.LogInfo($"arg1 {arg1}");
//     }

//     public static void Postfix(dynamic __instance) {
//         Plugin.Logger.LogInfo($"TalkMain::CommonMessageWindow:Postfix");

//         __instance.m_common_message_window = UnityEngine.Object.Instantiate<uCommonMessageWindow>(MainGameManager.Ref.MessageManager.Get00()).GetComponent<uCommonMessageWindow>();
//         __instance.m_common_message_window.Initialize(0);
//         __instance.m_common_message_window.SetLangMessage("The armoa is intoxicating!", uCommonMessageWindow.Pos.Center);

//         __instance.m_iconImage = iconObject.AddComponent<Image>();
//         __instance.m_iconImage.enabled = false;
//     }
// }

// [HarmonyPatch(typeof(TalkMain), "SetItem")]
// public static class Test4
// {
//     public static void Postfix(object[] __args, dynamic __instance)
//     {
//         Plugin.Logger.LogInfo($"TalkMain::SetItem");
//         Plugin.Logger.LogInfo($"__instance {__instance}");
//         Plugin.Logger.LogInfo(String.Join("\n", __args));

//         __args[0] = Plugin.ITEM_LOOKUP["Well Done Meat"].ToString();

//         uint itemID = Language.makeHash((string)__args[0]);
//         Plugin.Logger.LogInfo($"{Language.GetString(itemID)} [{itemID}]");
//     }
// }