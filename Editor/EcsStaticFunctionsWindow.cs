using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MoleHill.EcsCommands.Enums;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MoleHill.EcsCommands.Editor
{
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

                // ✅ Only when expanded: show "No World selected" warning
                if (_selectedWorld == null)
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.HelpBox("No World selected. Pick a World from the toolbar dropdown.", MessageType.Warning);
                    return; // nothing else to render
                }

                EditorGUILayout.Space(6);

                // ---- expanded UI continues below (injected info, parameters, run button, result, etc.) ----
                if (!TryGetFirstArgKind(cmd.Method, out var firstKind))
                {
                    EditorGUILayout.HelpBox("Unsupported first parameter. Use World, ref EntityManager, ref EntityCommandBuffer, or ParallelWriter.", MessageType.Error);
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

                EditorGUILayout.HelpBox(injected, MessageType.None);

                bool canRun = _selectedWorld != null;

                var parameters = cmd.Method.GetParameters();

                if (parameters.Length <= 1)
                {
                    EditorGUILayout.LabelField("No parameters.");
                }
                else
                {
                    for (int i = 1; i < parameters.Length; i++)
                    {
                        var param = parameters[i];

                        // injected lookups (including in/ref/out) => show disabled line
                        if (IsInjectedLookup(param))
                        {
                            using (new EditorGUI.DisabledScope(true))
                            {
                                EditorGUILayout.TextField(
                                    param.Name ?? StripByRef(param.ParameterType).Name,
                                    BuildLookupInjectionMessage(param));
                            }
                            continue;
                        }

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

            expanded = EditorGUILayout.Foldout(expanded, $"{key} ({structType.Name})", true);
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
                    return EditorGUILayout.Vector2Field(GUIContent.none, current is Vector2 v2 ? v2 : default);

                if (t == typeof(float3))
                    return EditorGUILayout.Vector3Field(GUIContent.none, current is Vector3 v3 ? v3 : default);

                if (t == typeof(float4))
                    return EditorGUILayout.Vector4Field(GUIContent.none, current is Vector4 v4 ? v4 : default);

                if (t == typeof(quaternion) || t == typeof(Quaternion))
                {
                    var q = current is Quaternion qq ? qq : Quaternion.identity;
                    // Show as Euler for usability
                    var euler = EditorGUILayout.Vector3Field(GUIContent.none, q.eulerAngles);
                    return Quaternion.Euler(euler);
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

            return false;
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
                var args = BuildArguments(cmd.Method, _selectedWorld, cmd.ParamValues, firstKind,  out var createdEcbForPlayback);

                // Invoke
                var result = cmd.Method.Invoke(null, args);
                CommitRefOutBack(cmd, cmd.Method, args);
                
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

        private static object?[] BuildArguments(
            MethodInfo method,
            World world,
            Dictionary<string, object?> values,
            FirstArgKind firstKind,
            out EntityCommandBuffer? createdEcbForPlayback)
        {
            createdEcbForPlayback = null;

            var ps = method.GetParameters();
            var args = new object?[ps.Length];

            // ---------- inject first argument ----------
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
                    throw new ArgumentOutOfRangeException(nameof(firstKind), firstKind, null);
            }

            // ---------- lookup provider system (public API) ----------
            var provider = world.GetOrCreateSystemManaged<EditorLookupProviderSystem>();
            provider.Update();

            static bool IsComponentLookupType(Type t)
                => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentLookup<>);

            static bool IsBufferLookupType(Type t)
                => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BufferLookup<>);

            static bool IsReadOnlyParam(ParameterInfo p)
                => p.GetCustomAttribute<ReadOnlyAttribute>() != null;

            MethodInfo? sysGetCompLookupDef = null;
            MethodInfo? sysGetBuffLookupDef = null;

            // ---------- remaining args ----------
            for (int i = 1; i < ps.Length; i++)
            {
                var p = ps[i];
                var pt = p.ParameterType;

                bool isByRef = pt.IsByRef;
                bool isOut = p.IsOut;
                var elemType = isByRef ? pt.GetElementType()! : pt;

                // ✅ Inject lookups even if they are in/ref/out
                if (IsComponentLookupType(elemType) || IsBufferLookupType(elemType))
                {
                    bool ro = EcsStaticFunctionsWindow.IsReadOnlyParam(p);
                    var genericArg = elemType.GetGenericArguments()[0];

                    if (IsComponentLookupType(elemType))
                    {
                        sysGetCompLookupDef ??= typeof(SystemBase)
                            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .First(m => m.Name == nameof(SystemBase.GetComponentLookup)
                                        && m.IsGenericMethodDefinition
                                        && m.GetParameters().Length == 1
                                        && m.GetParameters()[0].ParameterType == typeof(bool));

                        var lookup = sysGetCompLookupDef.MakeGenericMethod(genericArg)
                            .Invoke(provider, new object[] { ro });

                        // For out/ref/in, reflection still wants args[i] to contain a boxed value
                        args[i] = lookup;
                        continue;
                    }
                    else
                    {
                        sysGetBuffLookupDef ??= typeof(SystemBase)
                            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .First(m => m.Name == nameof(SystemBase.GetBufferLookup)
                                        && m.IsGenericMethodDefinition
                                        && m.GetParameters().Length == 1
                                        && m.GetParameters()[0].ParameterType == typeof(bool));

                        var lookup = sysGetBuffLookupDef.MakeGenericMethod(genericArg)
                            .Invoke(provider, new object[] { ro });

                        args[i] = lookup;
                        continue;
                    }
                }

                var key = p.Name ?? elemType.Name;

                // out param placeholder (Entity handled via commit-back)
                if (isOut)
                {
                    args[i] = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                    continue;
                }

                // normal/in/ref values from UI (Entity picker already stores Entity)
                if (!values.TryGetValue(key, out var v))
                {
                    if (elemType == typeof(Entity)) v = Entity.Null;
                    else if (p.HasDefaultValue) v = p.DefaultValue;
                    else v = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                }

                args[i] = v;
            }

            return args;
        }

        private static object? GetDefault(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
        
        private static string FriendlyLookupLabel(ParameterInfo p)
        {
            var t = p.ParameterType;
            var genericArg = t.IsGenericType ? t.GetGenericArguments()[0] : typeof(void);
            var ro = IsReadOnlyParam(p) ? "ReadOnly" : "ReadWrite";

            if (IsComponentLookupType(t))
                return $"Injected: ComponentLookup<{genericArg.Name}> ({ro})";

            if (IsBufferLookupType(t))
                return $"Injected: BufferLookup<{genericArg.Name}> ({ro})";

            return "Injected lookup";
        }
        
        private static void CommitRefOutBack(Command cmd, MethodInfo method, object?[] args)
        {
             var ps = method.GetParameters();

            for (int i = 1; i < ps.Length; i++)
            {
                var p = ps[i];
                if (!p.ParameterType.IsByRef) continue;

                // do not overwrite UI for `in`
                if (p.IsIn && !p.IsOut) continue;

                var elemType = p.ParameterType.GetElementType()!;
                var key = p.Name ?? elemType.Name;

                cmd.ParamValues[key] = args[i];
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

                        // REQUIRE: first param is World
                        if (!TryGetFirstArgKind(method, out _))
                            continue;

                        var displayName = string.IsNullOrWhiteSpace(attr.DisplayName)
                            ? $"{type.Name}.{method.Name}"
                            : attr.DisplayName!;

                        _commands.Add(new Command(method, displayName, attr.Category));
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

        private void RefreshWorlds(bool selectFirstIfNull)
        {
            // World.All returns the current set of worlds (including Editor World in edit-mode).
            // See Unity DOTS discussions; World.All is commonly used for editor tools. :contentReference[oaicite:1]{index=1}
            var worlds = World.All;
            
            if (_selectedWorld == null && selectFirstIfNull && worlds.Count > 0)
                _selectedWorld = worlds[0];
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
        private static string SafeWorldName(World w)
        {
            try
            {
                return string.IsNullOrWhiteSpace(w.Name) ? "<Unnamed World>" : w.Name;
            }
            catch
            {
                return "<Disposed World>";
            }
        }

        private sealed class Command
        {
            public readonly MethodInfo Method;
            public readonly string DisplayName;
            public readonly string? Category;
            public readonly Dictionary<string, object?> ParamValues = new();
            public readonly Dictionary<string, bool> StructFoldouts = new();
            
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