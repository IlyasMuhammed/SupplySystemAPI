import { FlowAnchorsComponent } from './flow-anchors.component';
import { FlowPageComponent } from './flow-page.component';
import { FlowPanelComponent } from './flow-panel.component';
import { FlowStagesComponent } from './flow-stages.component';

export { FlowAnchorsComponent, type FlowSection } from './flow-anchors.component';
export { FlowPageComponent } from './flow-page.component';
export { FlowPanelComponent } from './flow-panel.component';
export { FlowStagesComponent, flowStagesFrom, type FlowStage, type FlowStageState } from './flow-stages.component';
export { FlowCrumbsService } from './flow-crumbs.service';
export { flowTone, flowLabel, type FlowTone } from './flow-tone';

/** Everything a page needs: `imports: [...FLOW, …]`. */
export const FLOW = [FlowPageComponent, FlowStagesComponent, FlowAnchorsComponent, FlowPanelComponent] as const;
