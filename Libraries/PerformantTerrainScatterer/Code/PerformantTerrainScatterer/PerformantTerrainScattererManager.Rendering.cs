using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sandbox;

public sealed partial class PerformantTerrainScatterer
{
	private void RebuildRenderCache()
	{
		var newCache = new ChunkRenderData[_activeChunks.Count];
		int index = 0;

		foreach ( var chunk in _activeChunks.Values )
		{
			var batches = new ModelRenderBatch[chunk.TransformsByModel.Count];
			int bIndex = 0;

			foreach ( var kvp in chunk.TransformsByModel )
			{
				int mIdx = kvp.Key;
				batches[bIndex++] = new ModelRenderBatch
				{
					Model = ModelEntries[mIdx].Model,
					Transforms = kvp.Value,
					RenderDistSq = _modelRenderDistSq[mIdx],
					LodDistancesSq = _modelLodDistancesSq[mIdx],
					MaxLod = Math.Max( 0, _modelLodCounts[mIdx] - 1 )
				};
			}

			newCache[index++] = new ChunkRenderData { Center = chunk.Center, Batches = batches };
		}

		_renderCache = newCache;
	}

	private void RenderClutter( SceneObject obj )
	{
		var cache = _renderCache;
		if ( cache.Length == 0 ) return;

		if ( Scene == null ) return;
		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;

		Vector3 cameraPos = camera.WorldPosition;
		Vector3 cameraForward = camera.WorldRotation.Forward;

		float frustumMinDistSq = FrustumCullMinDistance * FrustumCullMinDistance;
		float maxRenderDistSq = _maxRenderDistance * _maxRenderDistance;
		bool useFrustum = UseFrustumCulling;

		if ( _lodBuckets == null )
		{
			_lodBuckets = new List<Transform>[16];
			for ( int i = 0; i < _lodBuckets.Length; i++ )
				_lodBuckets[i] = new List<Transform>();
		}

		for ( int c = 0; c < cache.Length; c++ )
		{
			ref var chunk = ref cache[c];

			Vector3 toChunk = chunk.Center - cameraPos;
			float chunkDistSq = toChunk.LengthSquared;

			if ( chunkDistSq > maxRenderDistSq ) continue;

			if ( useFrustum && chunkDistSq > frustumMinDistSq )
			{
				float dot = Vector3.Dot( cameraForward, toChunk );
				if ( dot < 0f && (dot * dot) > 0.04f * chunkDistSq )
					continue;
			}

			var batches = chunk.Batches;
			for ( int b = 0; b < batches.Length; b++ )
			{
				ref var batch = ref batches[b];
				if ( chunkDistSq > batch.RenderDistSq ) continue;

				int maxLod = batch.MaxLod;

				for ( int i = 0; i <= maxLod; i++ )
					_lodBuckets[i].Clear();

				var transforms = batch.Transforms;
				var lodDists = batch.LodDistancesSq;

				for ( int i = 0; i < transforms.Length; i++ )
				{
					Vector3 instPos = transforms[i].Position;
					float instDistSq = (instPos - cameraPos).LengthSquared;

					if ( instDistSq > batch.RenderDistSq ) continue;

					int lod = 0;
					for ( int l = 0; l < lodDists.Length; l++ )
					{
						if ( instDistSq > lodDists[l] )
							lod = l + 1;
						else
							break;
					}

					if ( lod > maxLod ) lod = maxLod;

					_lodBuckets[lod].Add( transforms[i] );
				}

				for ( int l = 0; l <= maxLod; l++ )
				{
					var bucket = _lodBuckets[l];
					if ( bucket.Count > 0 )
					{
						Graphics.DrawModelInstanced( batch.Model, CollectionsMarshal.AsSpan( bucket ), l );
					}
				}
			}
		}
	}
}
