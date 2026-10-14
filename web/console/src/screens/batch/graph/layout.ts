import { useEffect, useRef, useState } from 'react';
import type { BatchTaskBody } from '../../../api/batch';
import {
  graphEdges,
  graphShape,
  layerLayout,
  NODE_HEIGHT,
  NODE_WIDTH,
  type Positions,
} from '../model';

export type Engine = 'elk' | 'fallback';

export interface GraphLayout {
  positions: Positions;
  engine: Engine;
}

/** The slice of ELK's API this uses. */
interface Elk {
  layout(graph: unknown): Promise<{ children?: Array<{ id: string; x?: number; y?: number }> }>;
}

let elk: Promise<Elk | null> | null = null;

/**
 * ELK in a Web Worker started from the worker file the build ships as an asset of this origin: the page's policy allows a worker from
 * its own files (`script-src 'self'`) and refuses one from a blob. Without Worker support the graph falls back to a plain layering.
 */
function engine(): Promise<Elk | null> {
  elk ??= (async () => {
    if (typeof Worker === 'undefined') return null;
    const [api, worker] = await Promise.all([
      import('elkjs/lib/elk-api.js'),
      import('elkjs/lib/elk-worker.min.js?url'),
    ]);
    const url = worker.default;
    return new api.default({ workerFactory: () => new Worker(url) }) as Elk;
  })().catch(() => null);
  return elk;
}

/** The graph as ELK reads it: layered, left to right, with room between layers for the edges. */
export function elkGraph(tasks: readonly BatchTaskBody[]) {
  return {
    id: 'batch',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'RIGHT',
      'elk.spacing.nodeNode': '16',
      'elk.layered.spacing.nodeNodeBetweenLayers': '64',
      'elk.separateConnectedComponents': 'true',
      'elk.spacing.componentComponent': '24',
    },
    children: tasks.map((t) => ({ id: t.id, width: NODE_WIDTH, height: NODE_HEIGHT })),
    edges: graphEdges(tasks).map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };
}

export async function computeLayout(tasks: readonly BatchTaskBody[]): Promise<GraphLayout> {
  try {
    const instance = await engine();
    if (instance) {
      const result = await instance.layout(elkGraph(tasks));
      const positions: Positions = {};
      for (const child of result.children ?? [])
        positions[child.id] = { x: child.x ?? 0, y: child.y ?? 0 };
      if (tasks.every((t) => positions[t.id])) return { positions, engine: 'elk' };
    }
  } catch {
    // A worker that will not start, or a layout that fails, leaves the graph drawn the plain way.
  }
  return { positions: layerLayout(tasks), engine: 'fallback' };
}

/** The positions of the tasks. A poll that changes states but not the shape of the graph keeps its layout; null while the first one is computed. */
export function useGraphLayout(tasks: readonly BatchTaskBody[]): GraphLayout | null {
  const shape = graphShape(tasks);
  const latest = useRef(tasks);
  latest.current = tasks;
  const [done, setDone] = useState<(GraphLayout & { shape: string }) | null>(null);
  useEffect(() => {
    let live = true;
    computeLayout(latest.current).then((layout) => {
      if (live) setDone({ ...layout, shape });
    });
    return () => {
      live = false;
    };
  }, [shape]);
  return done?.shape === shape ? done : null;
}
