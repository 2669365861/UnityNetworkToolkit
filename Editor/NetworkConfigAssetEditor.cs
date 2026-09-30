using UnityEditor;
using UnityEngine;

namespace Common.Networking.EditorTools
{
    /// <summary>
    /// NetworkConfigAsset 的自定义 Inspector 面板。
    /// 功能：
    /// 1. 顶部提供「中文 / English」切换按钮，切换后整个面板的字段标题与提示立即变成对应语言；
    /// 2. 按分组绘制所有配置项，字段顺序与 NetworkConfig 中的声明保持一致；
    /// 3. 枚举型字段（TCP 协议、校验位、停止位）用本地化下拉框显示，避免只看到英文枚举名。
    /// 语言偏好保存在 EditorPrefs，不会写进配置资产。
    /// </summary>
    [CustomEditor(typeof(NetworkConfigAsset))]
    public sealed class NetworkConfigAssetEditor : UnityEditor.Editor
    {
        private static readonly string[] LanguageButtons = { "中文", "English" };

        private GUIStyle sectionTitleStyle;

        private void OnEnable()
        {
            // 语言切换时主动重绘，让按钮和标题立刻刷新
            NetworkConfigLocalization.LanguageChanged += Repaint;
        }

        private void OnDisable()
        {
            NetworkConfigLocalization.LanguageChanged -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawLanguageToolbar();

            SerializedProperty configProperty = serializedObject.FindProperty("config");
            if (configProperty == null)
            {
                EditorGUILayout.HelpBox(NetworkConfigLocalization.Pick(
                    "未找到 config 字段，请确认该资产是 NetworkConfigAsset 类型。",
                    "Field 'config' was not found. Make sure this asset is a NetworkConfigAsset."), MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            for (int i = 0; i < NetworkConfigLocalization.Sections.Length; i++)
            {
                ConfigSectionDefinition section = NetworkConfigLocalization.Sections[i];
                DrawSectionTitle(NetworkConfigLocalization.Pick(section.ChineseTitle, section.EnglishTitle));

                for (int j = 0; j < section.Fields.Length; j++)
                {
                    DrawField(configProperty, section.Fields[j]);
                }
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(NetworkConfigLocalization.Pick(
                "提示：连接时会把这份配置复制并校验后使用，不会修改工程里的原始资产。",
                "Note: this config is cloned and validated at runtime; the original asset is never modified."), MessageType.Info);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>绘制顶部的语言切换按钮和一行说明。</summary>
        private void DrawLanguageToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(NetworkConfigLocalization.Pick("界面语言", "Language"), GUILayout.Width(60f));

            int current = (int)NetworkConfigLocalization.Language;
            int selected = GUILayout.Toolbar(current, LanguageButtons, GUILayout.Height(20f));
            if (selected != current)
            {
                NetworkConfigLocalization.Language = (InspectorLanguage)selected;
                GUI.FocusControl(null);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(NetworkConfigLocalization.Pick(
                "语言切换只影响面板显示，不会写入配置资产。",
                "The language switch only affects the Inspector and is not saved into the asset."),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(2f);
        }

        /// <summary>绘制一个分组标题（带底色的横条）。</summary>
        private void DrawSectionTitle(string title)
        {
            if (sectionTitleStyle == null)
            {
                sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(6, 6, 1, 1)
                };
            }

            sectionTitleStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.88f, 0.88f, 0.88f)
                : new Color(0.12f, 0.12f, 0.12f);

            EditorGUILayout.Space(6f);
            Rect rect = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin
                ? new Color(0.26f, 0.26f, 0.26f)
                : new Color(0.80f, 0.80f, 0.80f));
            GUI.Label(rect, title, sectionTitleStyle);
        }

        /// <summary>按字段定义绘制单个配置项（普通字段、枚举字段、int 选项字段三种）。</summary>
        private static void DrawField(SerializedProperty configProperty, ConfigFieldDefinition field)
        {
            SerializedProperty property = configProperty.FindPropertyRelative(field.Name);
            if (property == null) return;

            GUIContent label = new GUIContent(
                NetworkConfigLocalization.Pick(field.ChineseLabel, field.EnglishLabel),
                NetworkConfigLocalization.Pick(field.ChineseTooltip, field.EnglishTooltip));

            // 没有选项定义 → 普通字段，交给 Unity 默认绘制
            if (field.ChineseOptions == null || field.EnglishOptions == null)
            {
                EditorGUILayout.PropertyField(property, label);
                return;
            }

            string[] options = NetworkConfigLocalization.IsChinese ? field.ChineseOptions : field.EnglishOptions;

            // OptionValues 为 null → 字段本身是枚举，直接用枚举下标做下拉框
            if (field.OptionValues == null)
            {
                int current = Mathf.Clamp(property.enumValueIndex, 0, options.Length - 1);
                int choice = EditorGUILayout.Popup(label, current, options);
                if (choice != property.enumValueIndex) property.enumValueIndex = choice;
                return;
            }

            // 否则字段是 int，按下标映射回真实数值（如 0=None 的校验位）
            int index = IndexOfValue(field.OptionValues, property.intValue);
            int selected = EditorGUILayout.Popup(label, index, options);
            if (selected != index) property.intValue = field.OptionValues[selected];
        }

        /// <summary>在取值表里查找当前值对应的下标；找不到时回退到第 0 项。</summary>
        private static int IndexOfValue(int[] values, int value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == value) return i;
            }
            return 0;
        }
    }
}
