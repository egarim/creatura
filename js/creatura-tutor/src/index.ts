// creatura-tutor: the emulated world of the first on-device app, a maths tutor for 4th grade (slice T0 of
// docs/design-mobile.md). Its tools and checks are the app's own TypeScript; the control layer is creatura-js's.

export { Rational, numberTokens, type NumberToken } from "./rational.js";
export { rng, mix, type Rng } from "./rng.js";
export { Answer } from "./answer.js";
export { Split, problem, problems, problemSeeds, splitOf, templates, mentions, type Problem, type Topic } from "./problems.js";
export { Voice, request, requests, voices, type Intent, type StudentRequest } from "./requests.js";
export { Fact, ToolId, tutorTools } from "./facts.js";
export { FaultyTools, TutorTools, type ToolResult, type TutorState } from "./tools.js";
export { Checks } from "./checks.js";
export { Barrier, TutorWorld, barriers, type ScreenLine } from "./world.js";
export { goal, tutorTask } from "./goal.js";
export { DoneReply, scriptedDriver } from "./driver.js";
export { judgedIn, runTurn, runUnguarded, type Judged, type TurnOptions, type TurnResult, type UnguardedResult } from "./turn.js";
