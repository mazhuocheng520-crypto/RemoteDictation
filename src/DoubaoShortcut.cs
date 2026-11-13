using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace RemoteDictation
{
    internal static class DoubaoShortcut
    {
        // Read-only mapping confirmed from ShortcutComboModel.ToBindingModifierFlags.
        internal static int ModifierIndex(int keyCode, int flags)
        {
            if (keyCode != 0) return -1;
            switch (flags) { case 1025: return 0; case 2049: return 1; case 258: return 2; case 514: return 3; default: return -1; }
        }
        internal static bool TryRead(out int index, out string message)
        {
            index = -1;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoubaoIME", "conf", "config.json");
                if (!File.Exists(path)) { message = "未找到豆包设置，请确认本机豆包输入法可用。"; return false; }
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                var voice = (Dictionary<string, object>)root["voice"];
                if (!Convert.ToBoolean(voice["enableGlobalVoiceShortcut"]) || !Convert.ToBoolean(voice["enableVoiceShortcut"]))
                { message = "豆包全局语音快捷键未启用，请先在豆包中检查。"; return false; }
                var shortcut = (Dictionary<string, object>)voice["voiceLongPressShortcut"];
                index = ModifierIndex(Convert.ToInt32(shortcut["keyCode"]), Convert.ToInt32(shortcut["modifierFlags"]));
                if (index < 0) { message = "当前豆包长按键不是单独的左右 Alt/Ctrl，请选择正确语音键后再试。"; return false; }
                message = "豆包语音键：" + new[] { "左 Alt", "右 Alt", "左 Ctrl", "右 Ctrl" }[index] + "（已读取）";
                return true;
            }
            catch { message = "暂时无法读取豆包快捷键，请在控制面板核对语音键。"; return false; }
        }
    }
}
