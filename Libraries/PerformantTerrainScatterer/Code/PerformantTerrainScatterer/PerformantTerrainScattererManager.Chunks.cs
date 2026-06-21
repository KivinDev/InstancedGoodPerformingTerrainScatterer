using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox;

public sealed partial class PerformantTerrainScatterer
{
	private void ManageChunks()
	{
		if ( Scene == null ) return;
		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return;
		Vector3 cameraPos = camera.WorldPosition;

		if ( _activeChunks.Count > 0 && cameraPos.DistanceSquared( _lastCameraPos ) < CameraUpdateThresholdSq )
			return;

		_lastCameraPos = cameraPos;
		int cameraCx = (int)MathF.Floor( cameraPos.x / ChunkSize );
		int cameraCy = (int)MathF.Floor( cameraPos.y / ChunkSize );
		int chunksInRadius = (int)MathF.Ceiling( ChunkLoadDistance / ChunkSize );

		for ( int dx = -chunksInRadius; dx <= chunksInRadius; dx++ )
		{
			for ( int dy = -chunksInRadius; dy <= chunksInRadius; dy++ )
			{
				var chunkCoord = (cameraCx + dx, cameraCy + dy);
				if ( !_activeChunks.ContainsKey( chunkCoord ) && _generatingChunks.Add( chunkCoord ) )
					_ = GenerateChunkAsync( chunkCoord.Item1, chunkCoord.Item2 );
			}
		}

		_chunkRemovalList.Clear();
		foreach ( var key in _activeChunks.Keys )
		{
			if ( Math.Abs( key.Item1 - cameraCx ) > chunksInRadius ||
			     Math.Abs( key.Item2 - cameraCy ) > chunksInRadius )
				_chunkRemovalList.Add( key );
		}

		foreach ( var t in _chunkRemovalList )
			_activeChunks.Remove( t );

		if ( _chunkRemovalList.Count > 0 )
			RebuildRenderCache();
	}

	private async Task GenerateChunkAsync( int cx, int cy )
	{
		try
		{
			if ( !IsValid || Scene == null ) return;
			Transform terrainTransform = TargetTerrain.IsValid() ? TargetTerrain.WorldTransform : new Transform();

			var tentativeInstances =
				await GameTask.RunInThreadAsync( () => CalculateChunkPlacements( cx, cy, terrainTransform ) );

			if ( !IsValid || Scene == null ) return;

			var modelEntries = ModelEntries;
			if ( tentativeInstances == null || modelEntries == null ) return;

			int cellsPerChunk = (int)MathF.Ceiling( ChunkSize / GridCellSize );
			int gridPadding = ObstructionBuffer;
			int gridWidth = cellsPerChunk + (gridPadding * 2) + 2;
			int gridArea = gridWidth * gridWidth;

			bool[] localBlockedGrid = ArrayPool<bool>.Shared.Rent( gridArea );
			Array.Clear( localBlockedGrid, 0, gridArea );

			try
			{
				int minGridX = (int)MathF.Floor( (cx * ChunkSize) / GridCellSize ) - gridPadding;
				int minGridY = (int)MathF.Floor( (cy * ChunkSize) / GridCellSize ) - gridPadding;
				int traceCounter = 0;
				bool needsTrace = CheckObstructions || SnapToGround;
				List<Transform>[] modelLists = new List<Transform>[modelEntries.Length];
				float cxSum = 0f, cySum = 0f, czSum = 0f;
				int placedCount = 0;
				Vector3 traceUpOffset = Vector3.Up * TraceStartHeight;

				foreach ( var inst in tentativeInstances )
				{
					if ( !IsValid || Scene == null ) return;

					Vector3 finalPosition = inst.Position;
					int gridX = (int)MathF.Floor( inst.Position.x / GridCellSize );
					int gridY = (int)MathF.Floor( inst.Position.y / GridCellSize );
					int localX = gridX - minGridX;
					int localY = gridY - minGridY;

					if ( CheckObstructions )
					{
						bool isBlocked = false;
						for ( int dx = -ObstructionBuffer; dx <= ObstructionBuffer; dx++ )
						{
							int nx = localX + dx;
							if ( nx < 0 || nx >= gridWidth ) continue;
							for ( int dy = -ObstructionBuffer; dy <= ObstructionBuffer; dy++ )
							{
								int ny = localY + dy;
								if ( ny < 0 || ny >= gridWidth ) continue;
								if ( localBlockedGrid[nx + ny * gridWidth] )
								{
									isBlocked = true;
									break;
								}
							}

							if ( isBlocked ) break;
						}

						if ( isBlocked ) continue;
					}

					if ( needsTrace )
					{
						var traceStart = inst.Position + traceUpOffset;
						var traceEnd = inst.Position - traceUpOffset;
						var trace = Scene.Trace.Ray( traceStart, traceEnd )
							.WithoutTags( TraceIgnoreTags )
							.Run();

						bool hitObstruction = trace.Hit && IsObstruction( trace.GameObject );

						if ( CheckObstructions && (trace.StartedSolid || hitObstruction) )
						{
							if ( localX >= 0 && localX < gridWidth && localY >= 0 && localY < gridWidth )
								localBlockedGrid[localX + localY * gridWidth] = true;
							continue;
						}

						if ( SnapToGround && trace.Hit && !trace.StartedSolid && !hitObstruction )
							finalPosition = trace.HitPosition;

						traceCounter++;
						if ( traceCounter % 100 == 0 )
						{
							await GameTask.Yield();
							if ( !IsValid || Scene == null ) return;
						}
					}

					if ( inst.ModelIndex < 0 || inst.ModelIndex >= modelEntries.Length ) continue;

					finalPosition += Vector3.Up * modelEntries[inst.ModelIndex].ZOffset;
					var list = modelLists[inst.ModelIndex] ??= new List<Transform>();
					list.Add( new Transform( finalPosition, inst.Rotation, inst.Scale ) );

					cxSum += finalPosition.x;
					cySum += finalPosition.y;
					czSum += finalPosition.z;
					placedCount++;
				}

				if ( !IsValid ) return;

				var chunk = new ClutterChunk();
				chunk.Center = placedCount > 0
					? new Vector3( cxSum / placedCount, cySum / placedCount, czSum / placedCount )
					: new Vector3( cx * ChunkSize + ChunkSize * 0.5f, cy * ChunkSize + ChunkSize * 0.5f, 0f );

				for ( int i = 0; i < modelLists.Length; i++ )
				{
					if ( modelLists[i] != null && modelLists[i].Count > 0 )
						chunk.TransformsByModel[i] = modelLists[i].ToArray();
				}

				_activeChunks[(cx, cy)] = chunk;
				RebuildRenderCache();
			}
			finally
			{
				ArrayPool<bool>.Shared.Return( localBlockedGrid );
			}
		}
		catch ( Exception )
		{
		}
		finally
		{
			_generatingChunks.Remove( (cx, cy) );
		}
	}
}
