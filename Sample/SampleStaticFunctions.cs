using MoleHill.EcsCommands.Enums;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace MoleHill.EcsCommands.Sample
{
    public static class SampleStaticFunctions
    {

        [EcsStaticFunction(category:"Sample : Test")]
        public static void TestOutParams(World world, ref int paramX, out int paramY)
        {
            paramY = paramX - 1;
            paramX += 1;
            
        }
        [System.Serializable]
        public struct CustomStruct
        {
            public int x;
        }
        
        [EcsStaticFunction(category: "Debug")]
        public static void Spawn(World world, int count,in EcsComponentAccess access, ref CustomStruct customStruct)
        {
            var em = world.EntityManager;
            var archetype = em.CreateArchetype();
            em.CreateEntity(archetype,count);
            Debug.Log($"Spawning {count} entities in {world.Name}");
        }
        
        [EcsStaticFunction(category: "Debug")]
        public static void DoThing(
            World world,
            [EcsEntityPicker(
                all:  new[] { typeof(LocalTransform), typeof(Static) },
                allAccess: EcsComponentAccess.ReadOnly,
                none: new[] { typeof(Disabled) }
            )]
            in Entity target,
            ref ComponentLookup<LocalTransform> transformLookup,
            [ReadOnly]in ComponentLookup<Static> staticLookup)
        {
            if (transformLookup.TryGetRefRW(target, out var transform))
            {
                transform.ValueRW.Position += new float3(0, 1, 0);
            }
        }

        [EcsStaticFunction(category: "Debug")]
        public static void CopyLocalTransform(World world,
            [EcsFromEntityRef("From")]in LocalTransform from,
            [EcsFromEntityRef("To",typeof(LocalToWorld))] ref LocalTransform to)
        {
            to = from;
        }

        [EcsStaticFunction(category: "Debug")]
        public static void ResetChildTransforms(ref EntityCommandBuffer ecb,
            in ComponentLookup<LocalTransform> transformLookup, 
            [EcsFromEntityRef("Parent")]in DynamicBuffer<Child> children)
        {
            foreach (var child in children)
            {
                if(!transformLookup.HasComponent(child.Value))
                    continue;
                ecb.SetComponent(child.Value, new LocalTransform(){Scale = 1});
            }
        }
        
        [EcsStaticFunction(category: "Debug")]
        public static void DoThingParallel(
            this ref EntityCommandBuffer.ParallelWriter ecb_pw,
            in int sortKey,
            [EcsEntityPicker(
                all:  new[] { typeof(LocalTransform), typeof(Static) },
                allAccess: EcsComponentAccess.ReadOnly,
                none: new[] { typeof(Disabled) }
            )]
            in Entity target,
            ref ComponentLookup<LocalTransform> transformLookup,
            [ReadOnly] in ComponentLookup<Static> staticLookup)
        {
            if(target == Entity.Null)
                throw new System.ArgumentNullException(nameof(target));
            if (transformLookup.TryGetRefRO(target, out var transform))
            {
                var t = transform.ValueRO;
                t.Position += new float3(0, 1, 0);
                ecb_pw.SetComponent(sortKey,target,t);
                
            }
        }
        
        
        
        [EcsStaticFunction(category: "Other")]
        public static void SpawnOther(ref EntityCommandBuffer ecb, int count)
        {
            for(int i = 0; i < count; i++)
                ecb.CreateEntity();
        }
    }
}