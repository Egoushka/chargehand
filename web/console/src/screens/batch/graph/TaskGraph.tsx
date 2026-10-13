import {
  BaseEdge,
  Controls,
  type Edge,
  type EdgeProps,
  getSmoothStepPath,
  Handle,
  type Node,
  type NodeProps,
  Position,
  ReactFlow,
} from '@xyflow/react';
import '@xyflow/react/dist/base.css';
import { useMemo } from 'react';
import type { BatchTaskBody } from '../../../api/batch';
import { formatUsd } from '../../../lib/format';
import type { Status } from '../../../lib/status';
import { cssVars, Skeleton, TaskNode } from '../../../ui';
import {
  type EdgeState,
  extent,
  graphEdges,
  NODE_HEIGHT,
  NODE_WIDTH,
  nodeProgress,
  taskMeta,
  taskUsd,
  taskWord,
} from '../model';
import { useGraphLayout } from './layout';
import './graph.css';

interface NodeData extends Record<string, unknown> {
  task: BatchTaskBody;
  status: Status;
  meta: string;
  selected: boolean;
  onSelect: (id: string) => void;
}

type TaskFlowNode = Node<NodeData, 'task'>;
type DepFlowEdge = Edge<{ state: EdgeState }, 'dep'>;

function TaskFlowNodeView({ data }: NodeProps<TaskFlowNode>) {
  const { task, status, meta, selected, onSelect } = data;
  const usd = taskUsd(task);
  const budget = task.budget?.usd;
  return (
    <>
      <Handle
        type="target"
        position={Position.Left}
        isConnectable={false}
        className="graph__handle"
      />
      <TaskNode
        id={task.id}
        goal={task.goal ?? ''}
        size={task.size ?? 'M'}
        status={status}
        progress={nodeProgress(task)}
        meta={meta}
        cost={
          usd == null
            ? undefined
            : budget != null
              ? `≈ ${formatUsd(usd)} / ${formatUsd(budget)}`
              : `≈ ${formatUsd(usd)}`
        }
        selected={selected}
        onSelect={() => onSelect(task.id)}
      />
      <Handle
        type="source"
        position={Position.Right}
        isConnectable={false}
        className="graph__handle"
      />
    </>
  );
}

function DepEdgeView({
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  data,
}: EdgeProps<DepFlowEdge>) {
  const [path] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
    borderRadius: 10,
  });
  return <BaseEdge path={path} className="ch-edge" data-state={data?.state} />;
}

const nodeTypes = { task: TaskFlowNodeView };
const edgeTypes = { dep: DepEdgeView };
const FIT = { padding: 0.08, maxZoom: 1 };

export interface TaskGraphProps {
  tasks: readonly BatchTaskBody[];
  selected: string | null;
  onSelect: (taskId: string) => void;
}

/**
 * The tasks as a graph of their `depends_on` links, laid out by ELK and drawn by React Flow. Nodes are the design system's task node, so a
 * node is a button (Tab, Enter and Space work) and the library's own focus handling is off. Wheel and drag move the view; the page keeps its scroll.
 */
export default function TaskGraph({ tasks, selected, onSelect }: TaskGraphProps) {
  const layout = useGraphLayout(tasks);
  const nodes = useMemo<TaskFlowNode[]>(
    () =>
      layout
        ? tasks.map((task) => ({
            id: task.id,
            type: 'task',
            position: layout.positions[task.id] ?? { x: 0, y: 0 },
            width: NODE_WIDTH,
            height: NODE_HEIGHT,
            initialWidth: NODE_WIDTH,
            initialHeight: NODE_HEIGHT,
            draggable: false,
            data: {
              task,
              status: taskWord(task, tasks),
              meta: taskMeta(task, tasks),
              selected: task.id === selected,
              onSelect,
            },
          }))
        : [],
    [layout, tasks, selected, onSelect],
  );
  const edges = useMemo<DepFlowEdge[]>(
    () =>
      graphEdges(tasks).map((e) => ({
        id: e.id,
        type: 'dep',
        source: e.source,
        target: e.target,
        data: { state: e.state },
      })),
    [tasks],
  );
  if (!layout) return <Skeleton width="100%" height={220} />;
  const box = extent(layout.positions);
  const height = Math.min(560, Math.max(220, box.height + 56));
  return (
    <div
      className="graph"
      data-layout={layout.engine}
      style={cssVars({ '--graph-h': height, '--graph-ar': box.width / Math.max(1, box.height) })}
    >
      <ReactFlow<TaskFlowNode, DepFlowEdge>
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        fitView
        fitViewOptions={FIT}
        minZoom={0.2}
        maxZoom={1.5}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        nodesFocusable={false}
        edgesFocusable={false}
        zoomOnScroll={false}
        preventScrolling={false}
        proOptions={{ hideAttribution: true }}
        aria-label="Task graph"
      >
        <Controls showInteractive={false} position="bottom-right" />
      </ReactFlow>
    </div>
  );
}
