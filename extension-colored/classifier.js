// Standalone mode's video classifier: a port of the service's ClassifierStore
// + RuleScoring (service/src/StudyBlocker.Service/Classification/), used by
// background.js only when the desktop app isn't installed, so YouTube is
// judged the same way with or without it. Same order: the channel lists,
// then the verdict cache, then the weighted score with the channel's
// learned bias. The word lists are in data/classifier-words.json (a service
// test keeps them equal to the service's). Not ported: education-only mode
// (the app's alone, the user's choice, 2026-11-21) and anything about
// locks, since standalone has none.

const SB_CLASSIFIER_KEY = "classifier";
const SB_CACHE_CAP = 1000;
const SB_LEARNED_MIN_SAMPLES = 5;
const SB_LEARNED_MIN_AGREEMENT = 0.8;
const SB_LEARNED_BIAS_POINTS = 2;
const SB_STUDY_THRESHOLD = 2;
const SB_DISTRACTION_THRESHOLD = -2;
const SB_KEYWORD_CAP = 4;

let sbWords = null;
async function sbClassifierWords() {
  if (!sbWords) {
    const words = await (await fetch(chrome.runtime.getURL("data/classifier-words.json"))).json();
    words.studyCategorySet = new Set(words.studyCategories.map((c) => c.toLowerCase()));
    words.distractionCategorySet = new Set(words.distractionCategories.map((c) => c.toLowerCase()));
    sbWords = words;
  }
  return sbWords;
}

const sbContains = (haystack, needle) => haystack.toLowerCase().includes(needle.toLowerCase());

function sbScore(meta, bias, words) {
  const haystack = `${meta.title ?? ""}\n${meta.description ?? ""}\n${(meta.keywords ?? []).join(" ")}`;

  const instant = words.instantDistractionKeywords.find((k) => sbContains(haystack, k));
  if (instant) return { verdict: "Distraction", reason: `instant-block keyword "${instant}" — always treated as distraction` };

  const grey = words.greySignals.find((s) => sbContains(haystack, s));
  if (grey) return { verdict: "Grey", reason: `looks like infotainment or a podcast (matched "${grey}")` };

  const studyHits = words.studyKeywords.filter((k) => sbContains(haystack, k));
  const distractionHits = words.distractionKeywords.filter((k) => sbContains(haystack, k));
  const category = meta.category ?? null;
  const categoryPoints = !category ? 0
    : words.studyCategorySet.has(category.toLowerCase()) ? 1
    : words.distractionCategorySet.has(category.toLowerCase()) ? -2
    : 0;
  const score = categoryPoints
    + Math.min(studyHits.length, SB_KEYWORD_CAP)
    - Math.min(distractionHits.length, SB_KEYWORD_CAP)
    + bias;

  const parts = [];
  if (categoryPoints) parts.push(`category "${category}" (${categoryPoints > 0 ? "+" : ""}${categoryPoints})`);
  if (studyHits.length) parts.push(`study words: ${studyHits.join(", ")}`);
  if (distractionHits.length) parts.push(`distraction words: ${distractionHits.join(", ")}`);
  if (bias) parts.push(`this channel usually runs ${bias > 0 ? "study" : "distraction"}`);
  const reason = `score ${score} (${parts.length ? parts.join("; ") : "no signals matched"})`;

  if (score >= SB_STUDY_THRESHOLD) return { verdict: "Study", reason };
  if (score <= SB_DISTRACTION_THRESHOLD) return { verdict: "Distraction", reason };
  return { verdict: "Grey", reason };
}

function sbLearnedBias(stats) {
  if (!stats) return 0;
  const [study, distraction] = stats;
  const total = study + distraction;
  if (total < SB_LEARNED_MIN_SAMPLES) return 0;
  if (study / total >= SB_LEARNED_MIN_AGREEMENT) return SB_LEARNED_BIAS_POINTS;
  if (distraction / total >= SB_LEARNED_MIN_AGREEMENT) return -SB_LEARNED_BIAS_POINTS;
  return 0;
}

// One classification at a time, so two videos judged together can't lose
// each other's update to the stored stats and cache.
let sbClassifyQueue = Promise.resolve();

// { verdict: "Study" | "Distraction" | "Grey", reason, source }
function sbClassify(meta, settings) {
  const run = sbClassifyQueue.then(() => sbClassifyNow(meta, settings));
  sbClassifyQueue = run.catch(() => {});
  return run;
}

async function sbClassifyNow(meta, settings) {
  if (!settings.enabled) return { verdict: "Study", reason: "switched off", source: "switched-off" };

  // Above everything: the user's own lists.
  const rule = sbFindChannel(settings.channels, meta.channelId, meta.channel);
  if (rule) {
    const allowed = rule.decision === "Allowed";
    return {
      verdict: allowed ? "Study" : "Distraction",
      reason: allowed ? "this channel is on your always-allowed list" : "you blocked this channel",
      source: "channel-rule",
    };
  }

  const words = await sbClassifierWords();

  const state = (await chrome.storage.local.get(SB_CLASSIFIER_KEY))[SB_CLASSIFIER_KEY] ?? { stats: {}, cache: {}, seq: 0 };
  const cached = state.cache[meta.id];
  if (cached) return { verdict: cached.verdict, reason: cached.reason, source: "cache" };

  const bias = meta.channelId ? sbLearnedBias(state.stats[meta.channelId]) : 0;
  const { verdict, reason } = sbScore(meta, bias, words);

  // Only rule-based, non-grey verdicts teach the channel.
  if (verdict !== "Grey" && meta.channelId) {
    const [study, distraction] = state.stats[meta.channelId] ?? [0, 0];
    state.stats[meta.channelId] = verdict === "Study" ? [study + 1, distraction] : [study, distraction + 1];
  }
  state.cache[meta.id] = { verdict, reason, seq: state.seq++ };
  const ids = Object.keys(state.cache);
  if (ids.length > SB_CACHE_CAP) {
    ids.sort((a, b) => state.cache[a].seq - state.cache[b].seq)
      .slice(0, ids.length - Math.floor(SB_CACHE_CAP * 0.9))
      .forEach((id) => delete state.cache[id]);
  }
  await chrome.storage.local.set({ [SB_CLASSIFIER_KEY]: state });
  return { verdict, reason, source: "rules" };
}
