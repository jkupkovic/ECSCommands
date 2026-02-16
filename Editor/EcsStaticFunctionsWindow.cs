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
        
        private readonly List<Command> _commands = new();
        private Vector2 _scroll;
        private string _search = "";

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
        
        private bool DrawEntityPicker(Command cmd, ParameterInfo p, string key, bool isIn)
        {
            if (_selectedWorld == null)
            {
                cmd.LastResultMessage = "No World selected.";
                cmd.LastResultType = MessageType.Warning;
                return false;
            }

            var em = _selectedWorld.EntityManager;

            // Read attribute (optional)
            var picker = p.GetCustomAttribute<EcsEntityPickerAttribute>();
            EntityQuery query;

            try
            {
                query = BuildEntityPickerQuery(em, picker);
            }
            catch (Exception ex)
            {
                cmd.LastResultMessage = ex.Message;
                cmd.LastResultType = MessageType.Error;
                return false;
            }

            // NOTE: In editor GUI, keep allocations modest. This is simplest + correct.
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);


            if (!cmd.ParamValues.TryGetValue(key, out var currentObj) || currentObj is not Entity current)
                current = Entity.Null;

            // Build labels
            int count = entities.Length;
            var labels = new string[count + 1];
            labels[0] = "<None>";

            int selectedIndex = 0;

            for (int i = 0; i < count; i++)
            {
                var e = entities[i];

                // Prefer EntityManager name if available; fallback to index/version
                string name = TryGetEntityName(em, e);
                labels[i + 1] = string.IsNullOrEmpty(name)
                    ? $"Entity ({e.Index}:{e.Version})"
                    : $"{name} ({e.Index}:{e.Version})";

                if (e == current)
                    selectedIndex = i + 1;
            }

            string label = $"{key} ({(isIn ? "in " : p.ParameterType.IsByRef ? "ref " : "")}Entity)";
            int newIndex = EditorGUILayout.Popup(label, selectedIndex, labels);

            if (newIndex <= 0)
                cmd.ParamValues[key] = Entity.Null;
            else
                cmd.ParamValues[key] = entities[newIndex - 1];

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
                EditorGUILayout.LabelField(cmd.DisplayName, EditorStyles.largeLabel);

                var declaring = cmd.Method.DeclaringType != null
                    ? cmd.Method.DeclaringType.FullName
                    : "<unknown type>";
                EditorGUILayout.LabelField(declaring, EditorStyles.miniLabel);

                EditorGUILayout.Space(6);

                // Params UI
                var parameters = cmd.Method.GetParameters();

                if (!TryGetFirstArgKind(cmd.Method, out var firstKind))
                {
                    EditorGUILayout.HelpBox("Unsupported first parameter. Use World, ref EntityManager, or ref EntityCommandBuffer.", MessageType.Error);
                    return;
                }

// Show what is injected as first argument
                string injected = firstKind switch
                {
                    FirstArgKind.World => "Injected: World (from toolbar selection)",
                    FirstArgKind.RefEntityManager => "Injected: ref EntityManager (from selected World)",
                    FirstArgKind.RefEntityCommandBuffer => "Injected: ref EntityCommandBuffer (auto-created; will Playback + Dispose)",
                    FirstArgKind.RefEntityCommandBufferParallelWriter => "Injected: ref EntityCommandBuffer.ParallelWriter (auto-created; will Playback + Dispose ECB)",
                    _ => "Injected: (unknown)"
                };

                EditorGUILayout.HelpBox(injected, MessageType.None);

                bool canRun = _selectedWorld != null;

// Draw ONLY parameters AFTER the injected first one
                if (parameters.Length <= 1)
                {
                    EditorGUILayout.LabelField("No parameters.");
                }
                else
                {
                    for (int i = 1; i < parameters.Length; i++)
                    {
                        var p = parameters[i];
                        
                        if (IsAutoInjectedParam(p))
                        {
                            using (new EditorGUI.DisabledScope(true))
                            {
                                // Show as disabled field for clarity
                                EditorGUILayout.TextField(p.Name ?? p.ParameterType.Name, FriendlyLookupLabel(p));
                            }
                            continue;
                        }
                        
                        
                        if (!TryDrawParamField(cmd, p))
                            canRun = false;
                    }
                }

                EditorGUILayout.Space(8);

                // Run
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!canRun || _selectedWorld == null))
                    {
                        if (GUILayout.Button("Run", GUILayout.Width(120), GUILayout.Height(24)))
                        {
                            RunCommand(cmd);
                        }
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
            bool isOut = p.IsOut;                 // out T
            bool isIn = p.IsIn && isByRef && !isOut; // in T (readonly ref)
            var elemType = isByRef ? pt.GetElementType()! : pt;

            // Auto-injected lookups are handled in the parameter loop (skip drawing).
            // If you don't skip them there, skip here too.
            if (IsAutoInjectedParam(p))
                return true;

            var key = p.Name ?? elemType.Name;

            // out parameters: no user input, but we still show a line
            if (isOut)
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
                return DrawEntityPicker(cmd, p, key, isIn);
            }

            // Normal supported types (also works for in/ref on these)
            cmd.ParamValues.TryGetValue(key, out var current);

            // For in parameters you can still edit what gets passed in (callee just can't write to it).
            // If you want in params to be non-editable, wrap DrawSupportedField in DisabledScope(true).
            object? newValue = DrawSupportedField($"{key} ({(isIn ? "in " : isByRef ? "ref " : "")}{elemType.Name})", elemType, current);

            if (ReferenceEquals(newValue, Unsupported))
            {
                cmd.LastResultMessage =
                    $"Unsupported parameter type: {(isByRef ? (isIn ? "in " : "ref ") : "")}{elemType.FullName}\n" +
                    "Supported base types: int, float, double, bool, string, enums, Vector2/3/4, Quaternion, Color, UnityEngine.Object, Entity.\n" +
                    "Also auto-injected: ComponentLookup<T>, BufferLookup<T>.";
                cmd.LastResultType = MessageType.Warning;
                return false;
            }

            cmd.ParamValues[key] = newValue;
            if (isOut)
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
            
            return true;
        }

        private static bool UnsupportedParam(Command cmd, ParameterInfo p)
        {
            cmd.LastResultMessage = $"Unsupported parameter type: {p.ParameterType.FullName}\n" +
                                    "Supported: int, float, double, bool, string, enums, Vector2/3/4, Quaternion, Color, UnityEngine.Object (references), " +
                                    "plus auto-injected World / EntityManager.";
            cmd.LastResultType = MessageType.Warning;
            return false;
        }

        private static readonly object Unsupported = new();

        private static object? DrawSupportedField(string label, Type t, object? current)
        {
            if (t == typeof(int))
                return EditorGUILayout.IntField(label, current is int i ? i : 0);

            if (t == typeof(float))
                return EditorGUILayout.FloatField(label, current is float f ? f : 0f);

            if (t == typeof(double))
            {
                double d = current is double dd ? dd : 0d;
                d = EditorGUILayout.DoubleField(label, d);
                return d;
            }

            if (t == typeof(bool))
                return EditorGUILayout.Toggle(label, current is bool b && b);

            if (t == typeof(string))
                return EditorGUILayout.TextField(label, current as string ?? "");

            if (t.IsEnum)
            {
                var e = current != null && current.GetType() == t
                    ? (Enum)current
                    : (Enum)Enum.GetValues(t).GetValue(0)!;

                return EditorGUILayout.EnumPopup(label, e);
            }

            if (t == typeof(float2))
                return EditorGUILayout.Vector2Field(label, current is Vector2 v2 ? v2 : default);

            if (t == typeof(float3))
                return EditorGUILayout.Vector3Field(label, current is Vector3 v3 ? v3 : default);

            if (t == typeof(float4))
                return EditorGUILayout.Vector4Field(label, current is Vector4 v4 ? v4 : default);

            if (t == typeof(Quaternion))
            {
                var q = current is Quaternion qq ? qq : Quaternion.identity;
                // Show as Euler for usability
                var euler = EditorGUILayout.Vector3Field(label + " (Euler)", q.eulerAngles);
                return Quaternion.Euler(euler);
            }

            if (t == typeof(Color))
                return EditorGUILayout.ColorField(label, current is Color c ? c : Color.white);

            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                return EditorGUILayout.ObjectField(label, current as UnityEngine.Object, t, allowSceneObjects: true);

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

                bool isByRef = pt.IsByRef;     // ref/out/in
                bool isOut = p.IsOut;
                var elemType = isByRef ? pt.GetElementType()! : pt;

                // Auto-inject lookups (by value)
                if (!isByRef && (IsComponentLookupType(elemType) || IsBufferLookupType(elemType)))
                {
                    bool ro = IsReadOnlyParam(p);
                    var genericArg = elemType.GetGenericArguments()[0];

                    if (IsComponentLookupType(elemType))
                    {
                        sysGetCompLookupDef ??= typeof(SystemBase)
                            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .First(m =>
                                m.Name == nameof(SystemBase.GetComponentLookup) &&
                                m.IsGenericMethodDefinition &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType == typeof(bool));

                        args[i] = sysGetCompLookupDef.MakeGenericMethod(genericArg)
                            .Invoke(provider, new object[] { ro });
                        continue;
                    }
                    else
                    {
                        sysGetBuffLookupDef ??= typeof(SystemBase)
                            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .First(m =>
                                m.Name == nameof(SystemBase.GetBufferLookup) &&
                                m.IsGenericMethodDefinition &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType == typeof(bool));

                        args[i] = sysGetBuffLookupDef.MakeGenericMethod(genericArg)
                            .Invoke(provider, new object[] { ro });
                        continue;
                    }
                }

                var key = p.Name ?? elemType.Name;

                // out param: pass default placeholder
                if (isOut)
                {
                    args[i] = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                    continue;
                }

                // normal / in / ref: load from UI values (or default)
                if (!values.TryGetValue(key, out var v))
                {
                    if (elemType == typeof(Entity))
                        v = Entity.Null;
                    else if (p.HasDefaultValue)
                        v = p.DefaultValue;
                    else
                        v = elemType.IsValueType ? Activator.CreateInstance(elemType) : null;
                }

                // For ref/in parameters: still pass boxed value in args[i]
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

            // NOTE: start at 1 only if you *always* inject param0.
            // If param0 can be user-managed ref/out, change start to 0.
            for (int i = 1; i < ps.Length; i++)
            {
                var p = ps[i];
                if (!p.ParameterType.IsByRef)
                    continue;

                // 'in' is readonly; don't overwrite UI value
                if (p.IsIn && !p.IsOut)
                    continue;

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
            => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ComponentLookup<>);

        private static bool IsBufferLookupType(Type t)
            => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BufferLookup<>);
        
        private static bool IsAutoInjectedParam(ParameterInfo p)
        {
            var t = p.ParameterType;
            return IsComponentLookupType(t) || IsBufferLookupType(t);
        }

        private static bool IsReadOnlyParam(ParameterInfo p)
        {
            // Unity.Collections.ReadOnlyAttribute
            return p.GetCustomAttribute<ReadOnlyAttribute>() != null;
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