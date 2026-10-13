import { cssVars, EmptyState, Icon } from '../../ui';
import { type GraphLayout, NODE_H, NODE_W, SIZES } from './model';

const isSize = (size: string): size is (typeof SIZES)[number] =>
  (SIZES as readonly string[]).includes(size);

/**
 * The batch's tasks in layers, an arrow from a task to each task that waits for it. A small drawing of the form, not a second editor:
 * the layers come from `depends_on`, so what is drawn is what the server will run first and what it will hold back.
 * Plain SVG and CSS: the console has no graph library, and this one preview is not reason to add one.
 */
export function DependencyGraph({ layout }: { layout: GraphLayout }) {
  if (layout.nodes.length === 0)
    return (
      <EmptyState
        icon="branch"
        title="No tasks to draw"
        text="Add a task and its dependencies show here."
      />
    );
  const waits = layout.edges.length;
  return (
    // biome-ignore lint/a11y/useSemanticElements: the region is a drawing, not a form group
    <div
      className="graph start-graph"
      // biome-ignore lint/a11y/noNoninteractiveTabindex: a region that scrolls sideways has to take the keyboard's focus
      tabIndex={0}
      role="group"
      aria-label={`Dependency graph: ${layout.nodes.length} ${layout.nodes.length === 1 ? 'task' : 'tasks'}, ${waits} ${waits === 1 ? 'dependency' : 'dependencies'}, ${layout.chain} ${layout.chain === 1 ? 'step' : 'steps'}`}
    >
      <div className="graph__inner" style={cssVars({ '--w': layout.width, '--h': layout.height })}>
        <svg
          width={layout.width}
          height={layout.height}
          viewBox={`0 0 ${layout.width} ${layout.height}`}
          aria-hidden="true"
        >
          {layout.edges.map((edge) => (
            <path
              key={`${edge.from}>${edge.to}`}
              className="ch-edge"
              data-state="waiting"
              d={edge.d}
            />
          ))}
        </svg>
        {layout.nodes.map((node) => (
          <div
            key={node.key}
            className="graph__n"
            style={cssVars({ '--x': node.x, '--y': node.y, '--nw': NODE_W, '--nh': NODE_H })}
          >
            <div className="start-node" data-layer={node.layer}>
              {isSize(node.size) ? (
                <span className="ch-size" data-size={node.size} title={`Size ${node.size}`}>
                  {node.size}
                </span>
              ) : (
                <span className="start-node__auto" title="No size: the planner or M decides">
                  auto
                </span>
              )}
              <span className="start-node__id" title={node.id}>
                {node.id || 'no id'}
              </span>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

/** The line under the graph when the dependencies loop. */
export function CycleNotice({ cycle }: { cycle: string[] }) {
  return (
    <div className="ch-banner" data-tone="danger" role="alert">
      <Icon name="alert-circle" />
      <div className="ch-banner__text">
        These tasks wait for each other: {cycle.join(' → ')}. The server refuses a loop as
        invalid_request.
      </div>
    </div>
  );
}
