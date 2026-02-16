# ECS Static Function Runner (Unity 6 / Entities)

Editor tooling for **Unity 6 DOTS (Entities)** that allows you to expose static methods as executable commands inside a custom Editor Window.

Run ECS logic directly from the Editor with automatic dependency injection for:

- `World`
- `ref EntityManager`
- `ref EntityCommandBuffer`
- `ref EntityCommandBuffer.ParallelWriter`
- `ComponentLookup<T>`
- `BufferLookup<T>`
- `Entity` (with component filtering)
- `ref`, `in`, and `out` parameters (including `Entity` and Lookups)

## Features
### Editor Window
<img width="712" height="688" alt="image" src="https://github.com/user-attachments/assets/5c1f0f8b-c922-4429-9cab-458ff9952dde" />

### Static Function Exposure

Mark static methods with:

```csharp
[EcsStaticFunction]
```

### World Selection

Select the active World from a toolbar dropdown.

### Category Filtering

Filter by category in the toolbar.

```csharp
[EcsStaticFunction(category: "Debug")]
```

### Entity Picker

Pick entities with given components.

```csharp
public static void DoAThing(
            World world,
            [EcsEntityPicker(
                all:  new[] { typeof(LocalTransform), typeof(Static) },
                allAccess: EcsComponentAccess.ReadOnly,
                none: new[] { typeof(Disabled) }
            )]
            in Entity target,
            ref ComponentLookup<LocalTransform> transformLookup,
            [ReadOnly] in ComponentLookup<Static> staticLookup
          )
{
            if (transformLookup.TryGetRefRW(target, out var transform))
            {
                transform.ValueRW.Position += new float3(0, 1, 0);
            }
}
```

### Automatic Injection
The following parameter types are injected automatically using selected world:

| Parameter Type                           | Behavior                                 |
| ---------------------------------------- | ---------------------------------------- |
| `World`                                  | Injected from toolbar                    |
| `ref EntityManager`                      | From selected world                      |
| `ref EntityCommandBuffer`                | Auto-created, playback & dispose         |
| `ref EntityCommandBuffer.ParallelWriter` | Auto-created, playback & dispose         |
| `ComponentLookup<T>`                     | Injected via provider system             |
| `BufferLookup<T>`                        | Injected via provider system             |

### Component references

Using attribute `EcsFromEntityRefAttribute` you are able to reference `IComponentData` of any entity with given component

```csharp
[EcsStaticFunction(category: "Debug")]
public static void CopyLocalTransform(World world,
    [EcsFromEntityRef("From")]in LocalTransform from,
    [EcsFromEntityRef("To",typeof(LocalToWorld))] ref LocalTransform to)
{
    to = from;
}
```

### Supported Method Signatures

First parameter must be one of:
- `World` world
- `ref EntityManager` em
- `ref EntityCommandBuffer` ecb
- `ref EntityCommandBuffer.ParallelWriter` writer

Other parameters may include:
- Primitive types
- `string`
- `enum`
- `float2`, `float3`, `float4`
- `quaternion`
- `Color`
- `UnityEngine.Object`
- `Entity`
- Custom `structs` of type `IComponentData`
- Custom `structs` (`public` fields & `[SerializeField]`)
- `ComponentLookup<T>`
- `BufferLookup<T>`
- `ref`, `in`, `out` variants of supported types

## Internal Architecture

A lightweight provider system is created automatically:
```csharp
public partial class EditorLookupProviderSystem : SystemBase
{
    protected override void OnUpdate() {}
}
```

Lookups are obtained via
```csharp
SystemBase.GetComponentLookup<T>()
SystemBase.GetBufferLookup<T>()
```
## Notes
* ECB uses `Allocator.TempJob`
* ECB playback happens automatically
* Only the first parameter may be injected (`World`,`EM`,`ECB`)
* Lookups respect `[ReadOnly]`
* Reflection-based invocation
* Foldable UI per function

