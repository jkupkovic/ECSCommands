using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MoleHill.EcsCommands.Enums;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MoleHill.EcsCommands.Editor
{
    #nullable enable
    public class EcsStaticFunctionsWindow : EditorWindow
    {
        // Requirement: "This world should be as variable in window class."
        [NonSerialized] private World? _selectedWorld;

        [SerializeField] private string _selectedCategory = "All";
        private readonly List<string> _cachedCategories = new();
        
        private const string FoldoutPrefsPrefix = "EcsStaticFnRunner.Foldout.";
        private readonly Dictionary<string, bool> _foldouts = new();
        

        
        private readonly List<Command> _commands = new();
        private Vector2 _scroll;
        private string _search = "";

        private static string GetCommandKey(MethodInfo m)
        {
            // stable-ish key across domain reloads: assembly + type + signature
            return $"{m.DeclaringType?.Assembly.FullName}|{m.DeclaringType?.FullName}|{m}";
        }
        private void SetFoldout(MethodInfo m, bool value)
        {
            var key = GetCommandKey(m);
            _foldouts[key] = value;
            EditorPrefs.SetBool(FoldoutPrefsPrefix + key, value);
        }
        private bool GetFoldout(MethodInfo m)
        {
            var key = GetCommandKey(m);
            if (_foldouts.TryGetValue(key, out var v))
                return v;

            v = EditorPrefs.GetBool(FoldoutPrefsPrefix + key, false);
            _foldouts[key] = v;
            return v;
        }
        
        [MenuItem("Tools/ECS/Static Functions")]
        public static void Open()
        {
            var w = GetWindow<EcsStaticFunctionsWindow>();
            w.titleContent = new GUIContent("ECS Static Functions");
            w.Show();
        }

        private void OnEnable()
        {
            RefreshCommands();
            RefreshWorlds(selectFirstIfNull: true);
        }

        private void OnHierarchyChange() => Repaint();
        private void OnProjectChange() => Repaint();

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(6);

            if (_selectedWorld == null)
            {
                EditorGUILayout.HelpBox("No World selected (or no worlds exist). Click Refresh Worlds.", MessageType.Warning);
            }

            DrawCommandsList();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                //GUILayout.FlexibleSpace();

                DrawWorldDropdown();
                DrawCategoryDropdown();
                
                if (GUILayout.Button("Expand All", EditorStyles.toolbarButton))
                {
                    foreach (var c in _commands)
                        SetFoldout(c.Method, true);
                }

                if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton))
                {
                    foreach (var c in _commands)
                        SetFoldout(c.Method, false);
                }

                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    RefreshWorlds(true);
                }
            }
        }

        private void DrawWorldDropdown()
        {
            var _cachedWorlds = World.All;
            if (_cachedWorlds.Count == 0)
            {
                if (GUILayout.Button("No Worlds", EditorStyles.toolbarDropDown, GUILayout.Width(220)))
                {
                    RefreshWorlds(true);
                }
                return;
            }

            string label = _selectedWorld != null
                ? _selectedWorld.Name
                : "Select World";

            if (GUILayout.Button(label, EditorStyles.toolbarDropDown, GUILayout.Width(220)))
            {
                ShowWorldMenu();
            }
        }
        
        private void ShowWorldMenu()
        {
            var _cachedWorlds = World.All;
            var menu = new GenericMenu();

            foreach (var world in _cachedWorlds)
            {
                bool isSelected = world == _selectedWorld;

                menu.AddItem(
                    new GUIContent(world.Name),
                    isSelected,
                    () =>
                    {
                        _selectedWorld = world;
                        Repaint();
                    });
            }

            if (_cachedWorlds.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No Worlds Found"));
            }

            menu.ShowAsContext();
        }
        
        private void DrawCommandsList()
        {
            IEnumerable<Command> filtered = _commands;

            if (_selectedCategory != "All")
            {
                filtered = filtered.Where(c =>
                {
                    var cat = string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category!;
                    return cat == _selectedCategory;
                });
            }
            
            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                filtered = filtered.Where(c =>
                    c.DisplayName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.Method.DeclaringType?.FullName?.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (c.Category?.IndexOf(s, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            // Group by category (optional)
            var groups = filtered
                .GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category!)
                .OrderBy(g => g.Key);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            foreach (var g in groups)
            {
                EditorGUILayout.LabelField(g.Key, EditorStyles.boldLabel);
                EditorGUILayout.Space(2);

                foreach (var cmd in g.OrderBy(c => c.DisplayName))
                {
                    DrawCommandCard(cmd);
                    EditorGUILayout.Space(6);
                }

                EditorGUILayout.Space(10);
            }

            EditorGUILayout.EndScrollView();
        }

        private static EntityQuery BuildEntityPickerQuery(EntityManager em, EcsEntityPickerAttribute? picker)
        {
            if (picker == null)
                return em.UniversalQuery;

            var all = ToComponentTypes(picker.All, picker.AllAccess);
            var any = ToComponentTypes(picker.Any, picker.AnyAccess);
            var none = ToComponentTypes(picker.None, EcsComponentAccess.Exclude); // always exclude

            var desc = new EntityQueryDesc
            {
                All  = all,
                Any  = any,
                None = none
            };

            return em.CreateEntityQuery(desc);
        }

        private static ComponentType[] ToComponentTypes(Type[] types, EcsComponentAccess access)
        {
            if (types == null || types.Length == 0)
                return Array.Empty<ComponentType>();

            var result = new ComponentType[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];

                // basic validation (optional but helpful)
                if (!typeof(IComponentData).IsAssignableFrom(t) &&
                    !typeof(IBufferElementData).IsAssignableFrom(t) &&
                    !typeof(ISharedComponentData).IsAssignableFrom(t))
                {
                    throw new ArgumentException($"Type {t.FullName} is not a DOTS component type.");
                }

                result[i] = access switch
                {
                    EcsComponentAccess.ReadOnly  => ComponentType.ReadOnly(t),
                    EcsComponentAccess.ReadWrite => ComponentType.ReadWrite(t),
                    EcsComponentAccess.Exclude   => ComponentType.Exclude(t),
                    _ => ComponentType.ReadOnly(t)
                };
            }
            return result;
        }
        
        private bool DrawEntityParam(Command cmd,ParameterInfo p, string key, bool isIn, bool isOut)
        {
            // out Entity = output only
            if (isOut)
            {
                cmd.ParamValues.TryGetValue(key, out var outVal);
                var e = outVal is Entity ent ? ent : Entity.Null;
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField($"{key} (out Entity)", e == Entity.Null ? "Entity.Null" : $"({e.Index}:{e.Version})");
                return true;
            }

            // in/ref/normal Entity = picker
            // Reuse your existing DrawEntityPicker logic, just store into cmd.ParamValues[key]
            return DrawEntityPickerForKey(cmd,p, key, isIn);
        }
        private static EntityQuery BuildEntityPickerQueryInline(EntityManager em, EcsEntityPickerAttribute? picker)
        {
            if (picker == null)
                return em.UniversalQuery;

            // Convert Type[] to ComponentType[] with ReadOnly/ReadWrite/Exclude
            ComponentType[] ToCT(Type[] types, EcsComponentAccess access)
            {
                if (types == null || types.Length == 0) return Array.Empty<ComponentType>();
                var arr = new ComponentType[types.Length];
                for (int i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    arr[i] = access switch
                    {
                        EcsComponentAccess.ReadOnly  => ComponentType.ReadOnly(t),
                        EcsComponentAccess.ReadWrite => ComponentType.ReadWrite(t),
                        EcsComponentAccess.Exclude   => ComponentType.Exclude(t),
                        _ => ComponentType.ReadOnly(t)
                    };
                }
                return arr;
            }

            var desc = new EntityQueryDesc
            {
                All  = ToCT(picker.All,  picker.AllAccess),
                Any  = ToCT(picker.Any,  picker.AnyAccess),
                None = ToCT(picker.None, EcsComponentAccess.Exclude)
            };

            return em.CreateEntityQuery(desc);
        }
        private bool DrawEntityPickerForKey(Command cmd,ParameterInfo p, string key, bool isIn)
        {
            if (_selectedWorld == null)
            {
                cmd.LastResultMessage = "No World selected.";
                cmd.LastResultType = MessageType.Warning;
                return false;
            }

            var em = _selectedWorld.EntityManager;

            // Read current value
            if (!cmd.ParamValues.TryGetValue(key, out var curObj) || curObj is not Entity current)
                current = Entity.Null;

            // Optional attribute filter
            var picker = p.GetCustomAttribute<EcsEntityPickerAttribute>();

            EntityQuery query;
            try
            {
                // If you already have this helper, keep using it:
                // query = BuildEntityPickerQuery(em, picker);
                //
                // Otherwise use the inline version below (safe & simple):
                query = BuildEntityPickerQueryInline(em, picker);
            }
            catch (Exception ex)
            {
                cmd.LastResultMessage = ex.Message;
                cmd.LastResultType = MessageType.Error;
                return false;
            }

            using var entities = query.ToEntityArray(Allocator.Temp);

            // Build popup labels
            int count = entities.Length;
            var labels = new string[count + 1];
            labels[0] = "<None>";

            int selectedIndex = 0;

            for (int i = 0; i < count; i++)
            {
                var e = entities[i];

                string name = TryGetEntityName(em, e);
                labels[i + 1] = string.IsNullOrEmpty(name)
                    ? $"Entity ({e.Index}:{e.Version})"
                    : $"{name} ({e.Index}:{e.Version})";

                if (e == current)
                    selectedIndex = i + 1;
            }

            // Label includes modifier for clarity
            string label = $"{key} ({(isIn ? "in " : p.ParameterType.IsByRef ? "ref " : "")}Entity)";

            int newIndex = EditorGUILayout.Popup(label, selectedIndex, labels);

            // Store selection
            cmd.ParamValues[key] = (newIndex <= 0) ? Entity.Null : entities[newIndex - 1];

            // If previous selection disappeared from query/world, show warning
            if (current != Entity.Null && selectedIndex == 0)
            {
                EditorGUILayout.HelpBox(
                    $"Previously selected entity is not present in the current World/query: ({current.Index}:{current.Version})",
                    MessageType.Warning);
            }

            return true;
        }

        private static string TryGetEntityName(EntityManager em, Entity e)
        {
            try
            {
                // Available in Entities packages; if not, this will throw and we fall back.
                return em.GetName(e);
            }
            catch
            {
                return "";
            }
        }

        private void DrawCommandCard(Command cmd)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                // Header row: foldout + quick run (disabled if no world)
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool expanded = GetFoldout(cmd.Method);

                    bool newExpanded = EditorGUILayout.Foldout(
                        expanded,
                        cmd.DisplayName,
                        toggleOnLabelClick: true,
                        style: EditorStyles.foldoutHeader);

                    if (newExpanded != expanded)
                        SetFoldout(cmd.Method, newExpanded);

                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(_selectedWorld == null))
                    {
                        if (GUILayout.Button("Run", GUILayout.Width(60)))
                            RunCommand(cmd);
                    }
                }

                // Mini label always visible
                var declaring = cmd.Method.DeclaringType != null ? cmd.Method.DeclaringType.FullName : "<unknown type>";
                EditorGUILayout.LabelField(declaring, EditorStyles.miniLabel);

                // When collapsed: show last result only (no "No World" warning here)
                if (!GetFoldout(cmd.Method))
                {
                    if (cmd.LastResultMessage != null)
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.HelpBox(cmd.LastResultMessage, cmd.LastResultType);
                    }
                    return;
                }

                // Only when expanded: show "No World selected" warning (and stop)
                if (_selectedWorld == null)
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.HelpBox("No World selected. Pick a World from the toolbar dropdown.", MessageType.Warning);
                    return;
                }

                // Entity References (only when world exists)
                if (cmd.EntityRefAll.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Entity References", EditorStyles.boldLabel);

                    foreach (var kv in cmd.EntityRefAll.OrderBy(k => k.Key))
                    {
                        var refKey = kv.Key;
                        var required = kv.Value;
                        DrawEntityRefPicker(cmd, refKey, required);
                    }

                    EditorGUILayout.Space(6);
                }

                EditorGUILayout.Space(6);

                // ---- expanded UI continues below ----
                if (!TryGetFirstArgKind(cmd.Method, out var firstKind))
                {
                    EditorGUILayout.HelpBox(
                        "Unsupported first parameter. Use World, ref EntityManager, ref EntityCommandBuffer, or ref EntityCommandBuffer.ParallelWriter.",
                        MessageType.Error);
                    return;
                }

                string injected = firstKind switch
                {
                    FirstArgKind.World => "Injected: World (from toolbar selection)",
                    FirstArgKind.RefEntityManager => "Injected: ref EntityManager (from selected World)",
                    FirstArgKind.RefEntityCommandBuffer => "Injected: ref EntityCommandBuffer (auto-created; Playback + Dispose)",
                    FirstArgKind.RefEntityCommandBufferParallelWriter => "Injected: ref EntityCommandBuffer.ParallelWriter (auto-created; Playback + Dispose)",
                    _ => "Injected: (unknown)"
                };

                if(firstKind != FirstArgKind.Other)
                    EditorGUILayout.HelpBox(injected, MessageType.None);

                bool canRun = true;

                var parameters = cmd.Method.GetParameters();

                if (parameters.Length <= 0)
                {
                    EditorGUILayout.LabelField("No parameters.");
                }
                else
                {
                    for (int i = firstKind == FirstArgKind.Other ? 0 : 1; i < parameters.Length; i++)
                    {
                        var param = parameters[i];

                        // -------------------------
                        // 1) EntityRef component/buffer params
                        // -------------------------
                        if (IsRefInjectedComponentOrBuffer(param))
                        {
                            var a = param.GetCustomAttribute<EcsFromEntityRefAttribute>();
                            var elem = StripByRef(param.ParameterType);

                            bool picked = a != null
                                          && cmd.ParamValues.TryGetValue(EntityRefValueKey(a.Reference), out var eobj)
                                          && eobj is Entity ent
                                          && ent != Entity.Null;

                            // If it's IComponentData and entity picked => draw actual value from selected entity
                            if (picked && IsComponentDataType(elem))
                            {
                                if (!DrawComponentFromEntityRef(cmd, param))
                                    canRun = false;

                                continue;
                            }

                            // If entity not picked => manual editing (your requirement)
                            if (!picked)
                            {
                                if (!TryDrawParamField(cmd, param))
                                    canRun = false;

                                continue;
                            }

                            // Picked but not component data (likely DynamicBuffer) => keep injected display for now
                            // (You can later add DrawBufferFromEntityRef similar to component)
                            string mod = param.IsOut ? "out " : (param.IsIn ? "in " : (param.ParameterType.IsByRef ? "ref " : ""));
                            string what =
                                IsComponentDataType(elem)
                                    ? $"{mod}{elem.Name}"
                                    : $"{mod}DynamicBuffer<{GetBufferElementType(elem)!.Name}>";

                            using (new EditorGUI.DisabledScope(true))
                                EditorGUILayout.TextField(param.Name ?? elem.Name, $"Injected from EntityRef '{a?.Reference}' → {what}");

                            continue;
                        }

                        // -------------------------
                        // 2) Injected lookups
                        // -------------------------
                        if (IsInjectedLookup(param))
                        {
                            using (new EditorGUI.DisabledScope(true))
                                EditorGUILayout.TextField(
                                    param.Name ?? StripByRef(param.ParameterType).Name,
                                    BuildLookupInjectionMessage(param));
                            continue;
                        }

                        // -------------------------
                        // 3) Normal params
                        // -------------------------
                        if (!TryDrawParamField(cmd, param))
                            canRun = false;
                    }
                }

                EditorGUILayout.Space(8);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(!canRun))
                    {
                        if (GUILayout.Button("Run", GUILayout.Width(120), GUILayout.Height(24)))
                            RunCommand(cmd);
                    }
                }

                if (cmd.LastResultMessage != null)
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.HelpBox(cmd.LastResultMessage, cmd.LastResultType);
                }
            }
        }

        private void DrawEntityRefPicker(Command cmd, string refKey, HashSet<Type> requiredAll)
        {
            if (_selectedWorld == null)
            {
                EditorGUILayout.HelpBox($"EntityRef '{refKey}': No World selected.", MessageType.Warning);
                return;
            }

            var em = _selectedWorld.EntityManager;

            // Build query: ALL required types
            var all = requiredAll
                .Where(t => t != null)
                .Select(ComponentType.ReadOnly)
                .ToArray();

            EntityQuery query = all.Length == 0
                ? em.UniversalQuery
                : em.CreateEntityQuery(new EntityQueryDesc { All = all });

            using var entities = query.ToEntityArray(Allocator.Temp);

            var storeKey = EntityRefValueKey(refKey);

            if (!cmd.ParamValues.TryGetValue(storeKey, out var curObj) || curObj is not Entity current)
                current = Entity.Null;

            var labels = new string[entities.Length + 1];
            labels[0] = $"<{refKey}: None>";

            int selectedIndex = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                string name = "";
                try { name = em.GetName(e); } catch { /* ignore */ }

                labels[i + 1] = string.IsNullOrEmpty(name)
                    ? $"{refKey}: Entity ({e.Index}:{e.Version})"
                    : $"{refKey}: {name} ({e.Index}:{e.Version})";

                if (e == current) selectedIndex = i + 1;
            }


            int newIndex = EditorGUILayout.Popup($"Ref '{refKey}' Entity", selectedIndex, labels);

            cmd.ParamValues[storeKey] = newIndex <= 0 ? Entity.Null : entities[newIndex - 1];
        }
        
        private static bool HasEntityRefAttr(ParameterInfo p, out EcsFromEntityRefAttribute attr)
        {
            attr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
            return attr != null;
        }

        private static bool IsRefInjectedComponentOrBuffer(ParameterInfo p)
        {
            var attr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
            if (attr == null) return false;

            var elem = StripByRef(p.ParameterType);
            return IsComponentDataType(elem) || IsDynamicBufferType(elem);
        }
        private bool TryDrawParamField(Command cmd, ParameterInfo p)
        {
            var pt = p.ParameterType;
            bool isByRef = pt.IsByRef;
            bool isOut = p.IsOut;
            bool isIn = p.IsIn && isByRef && !isOut;

            var elemType = isByRef ? pt.GetElementType()! : pt;

            // Auto-injected lookups are handled in the parameter loop (skip drawing).
            // If you don't skip them there, skip here too.
            if (IsInjectedLookup(p))
                return true;

            var key = p.Name ?? elemType.Name;

            // out parameters: no user input, but we still show a line
            if (isOut && elemType != typeof(Entity))
            {
                // Read whatever was written back after Run
                cmd.ParamValues.TryGetValue(key, out var outVal);

                string shown = outVal == null
                    ? "null"
                    : outVal.ToString();

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField($"{key} (out {elemType.Name})", shown);

                return true;
            }

            // Entity picker (works for Entity, in Entity, ref Entity)
            if (elemType == typeof(Entity))
            {
                // For byref Entity we still store an Entity in ParamValues[key]
                return DrawEntityParam(cmd,p, key, isIn, isOut);
            }
           

            // Normal supported types (also works for in/ref on these)
            cmd.ParamValues.TryGetValue(key, out var current);
            
            string mod = isOut ? "out " : isIn ? "in " : isByRef ? "ref " : "";
            string label = $"{key} ({mod}{elemType.Name})";
            
            // For in parameters you can still edit what gets passed in (callee just can't write to it).
            // If you want in params to be non-editable, wrap DrawSupportedField in DisabledScope(true).
            object? newValue = DrawSupportedField(label, elemType, current);

            if (ReferenceEquals(newValue, Unsupported))
            {
                if (elemType.IsValueType && !elemType.IsPrimitive && !elemType.IsEnum)
                {
                    object? boxed = current;
                    if (TryDrawStructParam(cmd, key, elemType, ref boxed))
                    {
                        cmd.ParamValues[key] = boxed;
                        return true;
                    }
                }

                cmd.LastResultMessage =
                    $"Unsupported parameter type: {elemType.FullName}\n\n" +
                    "Supported:\n" +
                    "- int, float, double, bool, string\n" +
                    "- enums\n" +
                    "- Vector2/3/4, Quaternion, Color\n" +
                    "- UnityEngine.Object references\n" +
                    "- Entity (incl. in/ref/out)\n" +
                    "- custom structs (public fields and [SerializeField] private fields)\n" +
                    "- auto-injected: ComponentLookup<T>, BufferLookup<T> (incl. in/ref/out)\n\n" +
                    "Note: ref/in/out is supported for the above value types.";
                cmd.LastResultType = MessageType.Warning;
                return false;
            }

            cmd.ParamValues[key] = newValue;
            
            return true;
        }
        
        private bool TryDrawStructParam(Command cmd, string key, Type structType, ref object? value)
        {
            if (!structType.IsValueType || structType.IsPrimitive || structType.IsEnum)
                return false;

            // Ensure value exists
            if (value == null || value.GetType() != structType)
                value = Activator.CreateInstance(structType);

            string foldKey = key + "|" + structType.FullName;
            if (!cmd.StructFoldouts.TryGetValue(foldKey, out bool expanded))
                expanded = false;

            
            expanded = EditorGUILayout.Foldout(expanded, $"[CustomStruct] {key} ({structType.Name})", true);
            cmd.StructFoldouts[foldKey] = expanded;

            if (!expanded)
                return true;

            EditorGUI.indentLevel++;
            

            object boxed = value; // boxed struct we will modify
            bool anyChanged = false;

            // Public instance fields + [SerializeField] private fields
            var fields = structType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f =>
                    !f.IsStatic &&
                    (f.IsPublic || f.GetCustomAttribute<SerializeField>() != null))
                .OrderBy(f => f.MetadataToken);

            foreach (var f in fields)
            {
                var ft = f.FieldType;
                object? fieldVal = f.GetValue(boxed);

                // Reuse your existing supported field drawer for primitives/enums/vectors/objects/etc.
                var drawn = DrawSupportedField(f.Name, ft, fieldVal);

                if (ReferenceEquals(drawn, Unsupported))
                {
                    // Nested struct support
                    if (ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum)
                    {
                        object? nested = fieldVal;
                        if (!TryDrawStructParam(cmd, $"{key}.{f.Name}", ft, ref nested))
                        {
                            EditorGUILayout.LabelField($"{f.Name} ({ft.Name})", "Unsupported");
                        }
                        else
                        {
                            f.SetValue(boxed, nested);
                            anyChanged = true;
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField($"{f.Name} ({ft.Name})", "Unsupported");
                    }

                    continue;
                }

                // Only write back if changed (simple compare)
                if (!Equals(drawn, fieldVal))
                {
                    f.SetValue(boxed, drawn);
                    anyChanged = true;
                }
            }

            EditorGUI.indentLevel--;
            
            if (anyChanged)
                value = boxed; // write back modified struct

            return true;
        }

        private static bool UnsupportedParam(Command cmd, ParameterInfo p)
        {
            cmd.LastResultMessage = $"Unsupported parameter type: {p.ParameterType.FullName}\n" +
                                    "Supported: int, float, double, bool, string, enums, float2/3/4, quaternion, Color, UnityEngine.Object (references), " +
                                    "plus auto-injected World / EntityManager.";
            cmd.LastResultType = MessageType.Warning;
            return false;
        }

        private static readonly object Unsupported = new();

        private static object? DrawSupportedField(string label, Type t, object? current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(220));

                if (t == typeof(int))
                    return EditorGUILayout.IntField( current is int i ? i : 0);

                if (t == typeof(float))
                    return EditorGUILayout.FloatField( current is float f ? f : 0f);

                if (t == typeof(double))
                {
                    double d = current is double dd ? dd : 0d;
                    d = EditorGUILayout.DoubleField( d);
                    return d;
                }

                if (t == typeof(bool))
                    return EditorGUILayout.Toggle( current is bool b && b);

                if (t == typeof(string))
                    return EditorGUILayout.TextField( current as string ?? "");

                if (t.IsEnum)
                {
                    var e = current != null && current.GetType() == t
                        ? (Enum)current
                        : (Enum)Enum.GetValues(t).GetValue(0)!;

                    return EditorGUILayout.EnumPopup( e);
                }

                if (t == typeof(float2))
                {
                    var v = current is float2 f ? f : default;
                    var uv = new Vector2(v.x, v.y);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector2Field(GUIContent.none, uv);
                    }
                    return new float2(uv.x, uv.y);
                }

                if (t == typeof(float3))
                {
                    var v = current is float3 f ? f : default;
                    var uv = new Vector3(v.x, v.y, v.z);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector3Field(GUIContent.none, uv);
                    }
                    return new float3(uv.x, uv.y, uv.z);
                }

                if (t == typeof(float4))
                {
                    var v = current is float4 f ? f : default;
                    var uv = new Vector4(v.x, v.y, v.z, v.w);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector4Field(GUIContent.none, uv);
                    }
                    return new float4(uv.x, uv.y, uv.z, uv.w);
                }
                
                if (t == typeof(int2))
                {
                    var v = current is int2 ii ? ii : default;
                    var uv = new Vector2Int(v.x, v.y);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector2IntField(GUIContent.none, uv);
                    }
                    return new int2(uv.x, uv.y);
                }

                if (t == typeof(int3))
                {
                    var v = current is int3 ii ? ii : default;
                    var uv = new Vector3Int(v.x, v.y, v.z);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector3IntField(GUIContent.none, uv);
                    }
                    return new int3(uv.x, uv.y, uv.z);
                }

                if (t == typeof(int4))
                {
                    var v = current is int4 ii ? ii : default;
                    var uv = new Vector4(v.x, v.y, v.z, v.w);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        uv = EditorGUILayout.Vector4Field(GUIContent.none, uv);
                    }
                    return new int4((int)uv.x, (int)uv.y, (int)uv.z, (int)uv.w);
                }

                if (t == typeof(quaternion) || t == typeof(Quaternion))
                {
                    var q = current is quaternion mq ? mq : quaternion.identity;
                    var uq = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
                    var euler = uq.eulerAngles;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(GUIContent.none, GUILayout.Width(220));
                        euler = EditorGUILayout.Vector3Field(GUIContent.none, euler);
                    }

                    var newUq = Quaternion.Euler(euler);
                    return new quaternion(newUq.x, newUq.y, newUq.z, newUq.w);
                }

                if (t == typeof(Color))
                    return EditorGUILayout.ColorField( current is Color c ? c : Color.white);

                if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                    return EditorGUILayout.ObjectField( current as UnityEngine.Object, t,
                        allowSceneObjects: true);

            }

            return Unsupported;
        }

        private static bool TryGetFirstArgKind(MethodInfo method, out FirstArgKind kind)
        {
            kind = default;

            var ps = method.GetParameters();
            if (ps.Length == 0) return false;

            var p0 = ps[0];
            var t0 = p0.ParameterType;

            if (t0 == typeof(World))
            {
                kind = FirstArgKind.World;
                return true;
            }

            // ref EntityManager
            if (t0.IsByRef && t0.GetElementType() == typeof(EntityManager))
            {
                kind = FirstArgKind.RefEntityManager;
                return true;
            }

            // ref EntityCommandBuffer
            if (t0.IsByRef && t0.GetElementType() == typeof(EntityCommandBuffer))
            {
                kind = FirstArgKind.RefEntityCommandBuffer;
                return true;
            }
            
            // ref EntityCommandBuffer.ParallelWriter
            if (t0.IsByRef && t0.GetElementType() == typeof(EntityCommandBuffer.ParallelWriter))
            {
                kind = FirstArgKind.RefEntityCommandBufferParallelWriter;
                return true;
            }

            kind = FirstArgKind.Other;
            return true;
        }
        
        private void RunCommand(Command cmd)
        {
            cmd.LastResultMessage = null;

            if (_selectedWorld == null)
            {
                cmd.LastResultMessage = "No World selected.";
                cmd.LastResultType = MessageType.Error;
                return;
            }

            try
            {
                if (!TryGetFirstArgKind(cmd.Method, out var firstKind))
                {
                    cmd.LastResultMessage = "Invalid command signature (unsupported first parameter).";
                    cmd.LastResultType = MessageType.Error;
                    return;
                }

                // Build args (inject first param based on signature)
                var args = BuildArguments(cmd,cmd.Method, _selectedWorld, cmd.ParamValues, firstKind,  out var createdEcbForPlayback);

                // Invoke
                var result = cmd.Method.Invoke(null, args);
                CommitRefOutBack(cmd, cmd.Method, args);
                CommitEntityRefComponentWrites(cmd, _selectedWorld, cmd.Method, args, firstKind == FirstArgKind.Other ? 0 : 1); 
                // If first arg is ref ECB, playback + dispose
                if (createdEcbForPlayback.HasValue)
                {
                    var ecb = createdEcbForPlayback.Value;
                    ecb.Playback(_selectedWorld.EntityManager);
                    ecb.Dispose();

                    cmd.LastResultMessage = result == null
                        ? "Ran successfully. ECB played back."
                        : $"Ran successfully. ECB played back. Result: {result}";
                }
                else
                {
                    cmd.LastResultMessage = result == null
                        ? "Ran successfully."
                        : $"Ran successfully. Result: {result}";
                }

                cmd.LastResultType = MessageType.Info;
            }
            catch (TargetInvocationException tie)
            {
                var ex = tie.InnerException ?? tie;
                cmd.LastResultMessage = ex.ToString();
                cmd.LastResultType = MessageType.Error;
            }
            catch (Exception ex)
            {
                cmd.LastResultMessage = ex.ToString();
                cmd.LastResultType = MessageType.Error;
            }
        }

        private static MethodInfo GetSystemGetComponentLookupOpen()
        {
            var methods = typeof(SystemBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == nameof(SystemBase.GetComponentLookup))
                .Where(m => m.IsGenericMethodDefinition)
                .Where(m => m.GetGenericArguments().Length == 1)
                .ToArray();

            // Prefer exact signature: GetComponentLookup<T>(bool)
            var exact = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 1 && ps[0].ParameterType == typeof(bool);
            });

            if (exact != null)
                return exact;

            // Fallback: any overload whose first parameter is bool (covers versions with extra optional params)
            var boolFirst = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType == typeof(bool);
            });

            if (boolFirst != null)
                return boolFirst;

            // Last resort: parameterless overload (if present in some version)
            var parameterless = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (parameterless != null)
                return parameterless;

            throw new MissingMethodException("Could not find a compatible SystemBase.GetComponentLookup<T>(...) overload.");
        }
        private static MethodInfo GetSystemGetBufferLookupOpen()
        {
            var methods = typeof(SystemBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == nameof(SystemBase.GetBufferLookup))
                .Where(m => m.IsGenericMethodDefinition)
                .Where(m => m.GetGenericArguments().Length == 1)
                .ToArray();

            var exact = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 1 && ps[0].ParameterType == typeof(bool);
            });

            if (exact != null)
                return exact;

            var boolFirst = methods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType == typeof(bool);
            });

            if (boolFirst != null)
                return boolFirst;

            var parameterless = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (parameterless != null)
                return parameterless;

            throw new MissingMethodException("Could not find a compatible SystemBase.GetBufferLookup<T>(...) overload.");
        }

        private static PropertyInfo GetIndexerItemEntityOrThrow(Type lookupType)
            => lookupType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(pi =>
                {
                    if (pi.Name != "Item") return false;
                    var idx = pi.GetIndexParameters();
                    return idx.Length == 1 && idx[0].ParameterType == typeof(Entity);
                });

        private static object?[] BuildArguments(
            Command cmd,
            MethodInfo method,
            World world,
            Dictionary<string, object?> values,
            FirstArgKind firstKind,
            out EntityCommandBuffer? createdEcbForPlayback)
        {
            createdEcbForPlayback = null;

            var ps = method.GetParameters();
            var args = new object?[ps.Length];
            var startIndex = 1;
            switch (firstKind)
            {
                case FirstArgKind.World:
                    args[0] = world;
                    break;

                case FirstArgKind.RefEntityManager:
                {
                    EntityManager em = world.EntityManager;
                    args[0] = em;
                    break;
                }

                case FirstArgKind.RefEntityCommandBuffer:
                {
                    var ecb = new EntityCommandBuffer(Allocator.TempJob);
                    args[0] = ecb;
                    createdEcbForPlayback = ecb;
                    break;
                }

                case FirstArgKind.RefEntityCommandBufferParallelWriter:
                {
                    var ecb = new EntityCommandBuffer(Allocator.TempJob);
                    args[0] = ecb.AsParallelWriter(); // boxed for ref
                    createdEcbForPlayback = ecb;
                    break;
                }

                default:
                    //throw new ArgumentOutOfRangeException(nameof(firstKind), firstKind, null);
                    startIndex = 0;
                    break;
            }

            var provider = world.GetOrCreateSystemManaged<EditorLookupProviderSystem>();
            provider.Update();
            
            // Cache method infos once per call
            var emGetCompOpen = GetEmGetComponentDataOpen();
            var sysGetCompLookupOpen = GetSystemGetComponentLookupOpen();
            var sysGetBuffLookupOpen = GetSystemGetBufferLookupOpen();

           for (int i = startIndex; i < ps.Length; i++)
            {
                var p = ps[i];
                var pt = p.ParameterType;

                bool isByRef = pt.IsByRef;
                bool isOut = p.IsOut;
                bool isIn = p.IsIn && isByRef && !isOut;
                var elemType = isByRef ? pt.GetElementType()! : pt;

                // ---------- 1) Auto-inject ComponentLookup<T> / BufferLookup<T> (supports in/ref/out) ----------
                // We inject regardless of in/ref/out; 'out' can overwrite it if user code assigns.
                var lookupBase = StripByRef(pt);
                if (IsComponentLookupType(lookupBase) || IsBufferLookupType(lookupBase))
                {
                    bool ro = IsReadOnlyParam(p);
                    var genericArg = lookupBase.GetGenericArguments()[0];

                    if (IsComponentLookupType(lookupBase))
                    {
                        args[i] = sysGetCompLookupOpen
                            .MakeGenericMethod(genericArg)
                            .Invoke(provider, new object[] { ro });
                        continue;
                    }

                    args[i] = sysGetBuffLookupOpen
                        .MakeGenericMethod(genericArg)
                        .Invoke(provider, new object[] { ro });
                    continue;
                }

                // ---------- 2) EntityRef injection for IComponentData / DynamicBuffer<T> with MANUAL FALLBACK ----------
                // If entity is NOT picked => do nothing here, fall through to normal/manual values.
                var refAttr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
                if (refAttr != null)
                {
                    var baseType = StripByRef(pt);

                    if (IsComponentDataType(baseType) || IsDynamicBufferType(baseType))
                    {
                        bool picked = TryGetPickedEntity(cmd, refAttr.Reference, out var ent) && ent != Entity.Null;

                        if (picked)
                        {
                            // out placeholder
                            if (isOut)
                            {
                                args[i] = baseType.IsValueType ? Activator.CreateInstance(baseType) : null;
                                continue;
                            }

                            // ---- IComponentData ----
                            if (IsComponentDataType(baseType))
                            {
                                // If UI drew/edited a value from entity, prefer cached component value.
                                // This allows "draw struct with values out of selected entity" and call with edited value.
                                var cacheKey = EntityRefComponentCacheKey(refAttr.Reference, baseType);
                                if (!isOut && values.TryGetValue(cacheKey, out var cached) && cached != null && cached.GetType() == baseType)
                                {
                                    args[i] = cached;
                                    continue;
                                }

                                var gmi = emGetCompOpen.MakeGenericMethod(baseType);
                                args[i] = gmi.Invoke(world.EntityManager, new object[] { ent });
                                continue;
                            }

                            // ---- DynamicBuffer<T> ----
                            if (IsDynamicBufferType(baseType))
                            {
                                bool ro = IsReadOnlyParam(p);
                                var be = GetBufferElementType(baseType)!;

                                var lookup = sysGetBuffLookupOpen
                                    .MakeGenericMethod(be)
                                    .Invoke(provider, new object[] { ro });

                                var indexer = GetIndexerItemEntityOrThrow(lookup!.GetType());
                                args[i] = indexer.GetValue(lookup, new object[] { ent });
                                continue;
                            }
                        }

                        // IMPORTANT: NOT picked -> fall through to normal/manual handling (no defaults, no continue)
                    }
                }

                // ---------- 3) Normal / manual parameters (supports in/ref/out) ----------
                // key uses parameter name; this matches your UI storage for manual inputs.
                var key = p.Name ?? elemType.Name;

                // out: pass placeholder (reflection will write back into args[i])
                if (isOut)
                {
                    args[i] = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                    continue;
                }

                // If user has entered a value, use it (also works for ref/in).
                if (!values.TryGetValue(key, out var v))
                {
                    // defaults if not provided
                    if (elemType == typeof(Entity))
                        v = Entity.Null;
                    else if (p.HasDefaultValue)
                        v = p.DefaultValue;
                    else
                        v = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                }

                args[i] = v;
            }

            return args;
            
        }

        private static bool TryGetPickedEntity(Command cmd, string refKey, out Entity e)
        {
            if (cmd.ParamValues.TryGetValue(EntityRefValueKey(refKey), out var obj) && obj is Entity ent)
            {
                e = ent;
                return ent != Entity.Null;
            }
            e = Entity.Null;
            return false;
        }

        private static void CommitRefOutBack(Command cmd, MethodInfo method, object?[] args)
        {
             var ps = method.GetParameters();

            
             for (int i = 1; i < ps.Length; i++)
             {
                 var p = ps[i];
                 var pt = p.ParameterType;

                 if (!pt.IsByRef && !p.IsOut)
                     continue;

                 // 'in' is readonly; don't overwrite from args
                 if (p.IsIn && !p.IsOut)
                     continue;

                 var elemType = StripByRef(pt);
                 var normalKey = p.Name ?? elemType.Name;

                 // 1) Always update the normal key so "ref struct" params update in UI
                 cmd.ParamValues[normalKey] = args[i];

                 // 2) If this param is EntityRef IComponentData, ALSO update the entity-ref cache key
                 var refAttr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
                 if (refAttr != null && typeof(IComponentData).IsAssignableFrom(elemType))
                 {
                     var cacheKey = EntityRefComponentCacheKey(refAttr.Reference, elemType);
                     cmd.ParamValues[cacheKey] = args[i];
                 }
             }
        }
        private void RefreshCommands()
        {
            _commands.Clear();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                    foreach (var method in type.GetMethods(flags))
                    {
                        var attr = method.GetCustomAttribute<EcsStaticFunctionAttribute>();
                        if (attr == null || !attr.ShowInWindow) continue;
                        if (!method.IsStatic || method.ContainsGenericParameters) continue;

                        if (!TryGetFirstArgKind(method, out _))
                            continue;

                        //Prevent duplicates
                        if(new Regex("\\$BurstManaged$").IsMatch(method.Name))
                            continue;
                        
                        var displayName = string.IsNullOrWhiteSpace(attr.DisplayName)
                            ? $"{type.Name}.{method.Name}"
                            : attr.DisplayName!;

                        var newCmd = new Command(method, displayName, attr.Category);
                        BuildEntityRefRequirements(newCmd);
                        _commands.Add(newCmd);
                    }
                }
            }

            RefreshCategories();
        }
        
        private void RefreshCategories()
        {
            _cachedCategories.Clear();
            _cachedCategories.Add("All");

            var cats = _commands
                .Select(c => string.IsNullOrWhiteSpace(c.Category) ? "Uncategorized" : c.Category!)
                .Distinct()
                .OrderBy(c => c);

            _cachedCategories.AddRange(cats);

            // Keep selection if possible, otherwise fallback
            if (!_cachedCategories.Contains(_selectedCategory))
                _selectedCategory = "All";
        }
        private static void BuildEntityRefRequirements(Command cmd)
        {
            cmd.EntityRefAll.Clear();

            var ps = cmd.Method.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                var attr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
                if (attr == null) continue;

                string refKey = attr.Reference;
                if (!cmd.EntityRefAll.TryGetValue(refKey, out var set))
                {
                    set = new HashSet<Type>();
                    cmd.EntityRefAll.Add(refKey, set);
                }

                // Add parameter-implied component requirement
                var elem = StripByRef(p.ParameterType);

                if (IsComponentDataType(elem))
                {
                    set.Add(elem);
                }
                else if (IsDynamicBufferType(elem))
                {
                    var be = GetBufferElementType(elem);
                    if (be != null) set.Add(be);
                }
                else
                {
                    // You only wanted this behavior for IComponentData and DynamicBuffer<T>.
                    // For other param types, we ignore the attribute (or you can warn).
                }

                // Add extra requirements
                foreach (var t in attr.ExtraAll)
                    if (t != null) set.Add(t);
            }
        }
        private void DrawCategoryDropdown()
        {
            string label = $"Category: {_selectedCategory}";
            if (GUILayout.Button(label, EditorStyles.toolbarDropDown, GUILayout.Width(180)))
            {
                var menu = new GenericMenu();
                foreach (var cat in _cachedCategories)
                {
                    bool selected = cat == _selectedCategory;
                    menu.AddItem(new GUIContent(cat), selected, () =>
                    {
                        _selectedCategory = cat;
                        Repaint();
                    });
                }
                menu.ShowAsContext();
            }
        }
        
        private static void CommitEntityRefComponentWrites(Command cmd, World world, MethodInfo method, object?[] args,int startIndex= 1)
        {
            var em = world.EntityManager;
            var ps = method.GetParameters();
            
            for (int i = startIndex; i < ps.Length; i++)
            {
                var p = ps[i];
                var refAttr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
                if (refAttr == null) continue;

                var elem = StripByRef(p.ParameterType);
                if (!IsComponentDataType(elem)) continue;             // only components write-back
                if (!p.ParameterType.IsByRef && !p.IsOut) continue;   // only ref/out
                if (p.IsIn && !p.IsOut) continue;                     // don't write back 'in'

                if (!TryGetPickedEntity(cmd, refAttr.Reference, out var e))
                    continue;

                // args[i] contains updated boxed component for ref/out after Invoke
                if (args[i] == null) continue;

                var gmi = s_EmSetComponentDataOpen.MakeGenericMethod(elem);
                gmi.Invoke(em, new object[] { e, args[i]! });
            }
        }
        private static string EntityRefValueKey(string refKey) => $"@entityref:{refKey}";
        private void RefreshWorlds(bool selectFirstIfNull)
        {
            var worlds = World.All;
            
            if (_selectedWorld == null && selectFirstIfNull && worlds.Count > 0)
                _selectedWorld = worlds[0];
        }

        private static bool IsDynamicBufferType(Type t)
        {
            t = StripByRef(t);
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DynamicBuffer<>);
        }

        private static bool IsComponentDataType(Type t)
        {
            t = StripByRef(t);
            return typeof(IComponentData).IsAssignableFrom(t);
        }

        private static Type? GetBufferElementType(Type t)
        {
            t = StripByRef(t);
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(DynamicBuffer<>))
                return null;
            return t.GetGenericArguments()[0];
        }

        private static bool IsComponentLookupType(Type t)
        {
            t = StripByRef(t);
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentLookup<>);
        }

        private static bool IsBufferLookupType(Type t)
        {
            t = StripByRef(t);
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BufferLookup<>);
        }

        private static bool IsInjectedLookup(ParameterInfo p)
        {
            var t = StripByRef(p.ParameterType);
            return IsComponentLookupType(t) || IsBufferLookupType(t);
        }

        private static string ParamModifier(ParameterInfo p)
        {
            if (!p.ParameterType.IsByRef) return "";
            if (p.IsOut) return "out ";
            if (p.IsIn) return "in ";
            return "ref ";
        }
        
        private static bool IsReadOnlyParam(ParameterInfo p)
        {
            // Unity.Collections.ReadOnlyAttribute
            return p.GetCustomAttribute<ReadOnlyAttribute>() != null;
        }
        
        private static Type StripByRef(Type t) => t.IsByRef ? t.GetElementType()! : t;
        private static string BuildLookupInjectionMessage(ParameterInfo p)
        {
            var mod = ParamModifier(p);
            var t = StripByRef(p.ParameterType);
            var genericArg = t.GetGenericArguments()[0];
            bool ro = IsReadOnlyParam(p);

            string kind = IsComponentLookupType(t)
                ? $"ComponentLookup<{genericArg.Name}>"
                : $"BufferLookup<{genericArg.Name}>";

            // Explicitly mention [ReadOnly]
            return ro
                ? $"Injected from selected World — {mod}{kind} with [ReadOnly]"
                : $"Injected from selected World — {mod}{kind} (ReadWrite)";
        }
        private static string EntityRefComponentCacheKey(string refKey, Type compType)
            => $"@entityrefcomp:{refKey}:{compType.AssemblyQualifiedName}";
        
        private static MethodInfo GetEmGetComponentDataOpen()
        {
            return typeof(EntityManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m =>
                {
                    if (m.Name != "GetComponentData") return false;
                    if (!m.IsGenericMethodDefinition) return false;
                    if (m.GetGenericArguments().Length != 1) return false;

                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == typeof(Entity);
                });
        }
        
        private bool DrawComponentFromEntityRef(Command cmd, ParameterInfo p)
        {
            if (_selectedWorld == null) return false;

            var refAttr = p.GetCustomAttribute<EcsFromEntityRefAttribute>();
            if (refAttr == null) return false;

            var pt = p.ParameterType;
            bool isByRef = pt.IsByRef;
            bool isOut = p.IsOut;
            bool isIn = p.IsIn && isByRef && !isOut;

            var compType = StripByRef(pt);
            if (!typeof(IComponentData).IsAssignableFrom(compType))
                return false;

            // If EntityRef not picked => fall back to normal param drawer (manual editing)
            if (!TryGetPickedEntity(cmd, refAttr.Reference, out var ent) || ent == Entity.Null)
                return false;

            var em = _selectedWorld.EntityManager;
            var cacheKey = EntityRefComponentCacheKey(refAttr.Reference, compType);

            // Refresh cached value when missing OR wrong type OR entity changed
            // Track entity change using another key
            var entKey = cacheKey + ":entity";
            bool entityChanged = !cmd.ParamValues.TryGetValue(entKey, out var prevEntObj)
                                 || prevEntObj is not Entity prevEnt
                                 || prevEnt != ent;

            if (entityChanged || !cmd.ParamValues.TryGetValue(cacheKey, out var cached) || cached == null || cached.GetType() != compType)
            {
                // Out param has no input value; show placeholder (read-only) until after Run
                if (isOut)
                {
                    cmd.ParamValues[cacheKey] = Activator.CreateInstance(compType);
                }
                else
                {
                    var gmi = s_EmGetComponentDataOpen.MakeGenericMethod(compType);
                    cmd.ParamValues[cacheKey] = gmi.Invoke(em, new object[] { ent });
                }

                cmd.ParamValues[entKey] = ent;
            }

            // Draw it
            var label = $"{p.Name ?? compType.Name} ({(isOut ? "out " : isIn ? "in " : (isByRef ? "ref " : ""))}{compType.Name})";
            object? value = cmd.ParamValues[cacheKey];

            // in/out should be read-only in UI (out gets filled after Run)
            using (new EditorGUI.DisabledScope(isIn || isOut))
            {
                // First try primitive/vector drawer, then struct drawer fallback
                var drawn = DrawSupportedField(label, compType, value);
                if (ReferenceEquals(drawn, Unsupported))
                {
                    var boxed = value;
                    if (TryDrawStructParam(cmd, cacheKey, compType, ref boxed))
                        cmd.ParamValues[cacheKey] = boxed;
                    else
                        EditorGUILayout.LabelField(label, "Unsupported");
                }
                else
                {
                    cmd.ParamValues[cacheKey] = drawn;
                }
            }

            // Show where it came from
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Source", $"EntityRef '{refAttr.Reference}' → {ent.Index}:{ent.Version}");

            return true; // we handled UI for this parameter
        }

        private static MethodInfo GetEmSetComponentDataOpen()
        {
            return typeof(EntityManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m =>
                {
                    if (m.Name != "SetComponentData") return false;
                    if (!m.IsGenericMethodDefinition) return false;
                    if (m.GetGenericArguments().Length != 1) return false;

                    var ps = m.GetParameters();
                    return ps.Length == 2
                           && ps[0].ParameterType == typeof(Entity)
                           && ps[1].ParameterType.IsGenericParameter; // T
                });
        }

        private static readonly MethodInfo s_EmGetComponentDataOpen =
            typeof(EntityManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m =>
                {
                    if (m.Name != "GetComponentData") return false;
                    if (!m.IsGenericMethodDefinition) return false;
                    if (m.GetGenericArguments().Length != 1) return false;
                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == typeof(Entity);
                });
        private static readonly MethodInfo s_EmSetComponentDataOpen = GetEmSetComponentDataOpen();

        private sealed class Command
        {
            public readonly MethodInfo Method;
            public readonly string DisplayName;
            public readonly string? Category;
            public readonly Dictionary<string, object?> ParamValues = new();
            public readonly Dictionary<string, bool> StructFoldouts = new();
            public readonly Dictionary<string, HashSet<Type>> EntityRefAll = new(); 
            
            public string? LastResultMessage;
            public MessageType LastResultType = MessageType.None;

            public Command(MethodInfo method, string displayName, string? category)
            {
                Method = method;
                DisplayName = displayName;
                Category = category;
            }
        }
    }
}