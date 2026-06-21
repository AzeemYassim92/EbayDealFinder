const storageKey = "p2w.auctionScan.settings.v2";

const defaults = {
  grades: ["psa10"],
  endingWithinHours: 2,
  allowWindowExpansion: false,
  fallbackEndingWithinHours: 6,
  pagesPerGrade: 1,
  take: 100,
  minMarketValue: 50,
  maxMarketValue: 250,
  minCurrentBid: 1,
  maxCurrentBid: 250,
  minProfit: 10,
  minMarginPercent: 10,
  minRoiPercent: 10,
  minMatchScore: 75,
  minProductYearlyVolume: 0,
  feePercent: 13.25,
  fixedFee: 0.30,
  outboundShippingCost: 5.00,
  packingCost: 1.00,
  bufferCost: 2.00,
  includeBuyNowComparison: false,
  buyNowComparisonLimit: 10,
  showOnlyViableAuctions: false,
  sortBy: "ending",
  query: ""
};

const els = {
  status: document.querySelector("#scanStatus"),
  metrics: document.querySelector("#scanMetrics"),
  title: document.querySelector("#resultTitle"),
  applied: document.querySelector("#appliedRequest"),
  rows: document.querySelector("#auctionRows"),
  gradeRuns: document.querySelector("#gradeRuns"),
  rejectionSummary: document.querySelector("#rejectionSummary"),
  runScan: document.querySelector("#runScan"),
  resetDefaults: document.querySelector("#resetDefaults"),
  fallbackLabel: document.querySelector("#fallbackLabel"),
  gradeInputs: Array.from(document.querySelectorAll("[data-grade]"))
};

const fields = [
  "endingWithinHours",
  "allowWindowExpansion",
  "fallbackEndingWithinHours",
  "pagesPerGrade",
  "take",
  "minMarketValue",
  "maxMarketValue",
  "minCurrentBid",
  "maxCurrentBid",
  "minProfit",
  "minMarginPercent",
  "minRoiPercent",
  "minMatchScore",
  "minProductYearlyVolume",
  "feePercent",
  "fixedFee",
  "outboundShippingCost",
  "packingCost",
  "bufferCost",
  "includeBuyNowComparison",
  "buyNowComparisonLimit",
  "showOnlyViableAuctions",
  "sortBy",
  "query"
];

let lastPayload = null;
let countdownTimer = null;

initHourOptions();
loadSettings();
wireEvents();
renderEmpty();

function initHourOptions() {
  for (const id of ["endingWithinHours", "fallbackEndingWithinHours"]) {
    const select = document.querySelector(`#${id}`);
    select.innerHTML = Array.from({ length: 24 }, (_, index) => {
      const hour = index + 1;
      return `<option value="${hour}">${hour} ${hour === 1 ? "hour" : "hours"}</option>`;
    }).join("");
  }
}

function wireEvents() {
  els.runScan.addEventListener("click", () => runScan());
  els.resetDefaults.addEventListener("click", () => {
    applySettings(defaults);
    saveSettings();
    renderEmpty();
  });
  document.querySelector("#allowWindowExpansion").addEventListener("change", () => {
    syncFallbackState();
    saveSettings();
  });
  document.querySelector("#sortBy").addEventListener("change", () => {
    saveSettings();
    if (lastPayload) render(lastPayload);
  });
  for (const id of fields) {
    const el = document.querySelector(`#${id}`);
    if (!el || id === "allowWindowExpansion" || id === "sortBy") continue;
    el.addEventListener("change", saveSettings);
  }
  for (const input of els.gradeInputs) input.addEventListener("change", saveSettings);
}

async function runScan() {
  saveSettings();
  setLoading();
  const request = collectSettings();

  try {
    const response = await fetch("/api/scan/graded-auctions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify(request)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.errorMessage || "Auction scan request failed.");
    lastPayload = data;
    render(data);
  } catch (error) {
    stopCountdown();
    els.status.textContent = "Auction scan failed";
    els.status.className = "provider-pill blocked";
    els.title.textContent = "Auction scan failed";
    els.rows.innerHTML = `<div class="empty-state">${escapeHtml(error.message)}</div>`;
    els.metrics.innerHTML = "";
    els.gradeRuns.innerHTML = "";
    els.rejectionSummary.innerHTML = "";
  }
}

function setLoading() {
  stopCountdown();
  els.status.textContent = "Scanning graded auctions";
  els.status.className = "provider-pill";
  els.metrics.innerHTML = "";
  els.applied.innerHTML = "";
  els.title.textContent = "Checking eBay auctions ending soon";
  els.rows.innerHTML = `<div class="empty-state">Fetching grade-specific auction pages, parsing time left, matching the local catalog, then calculating bid ceilings...</div>`;
  els.gradeRuns.innerHTML = "";
  els.rejectionSummary.innerHTML = "";
}

function renderEmpty() {
  stopCountdown();
  els.status.textContent = "Ready";
  els.status.className = "provider-pill";
  els.metrics.innerHTML = "";
  els.applied.innerHTML = "";
  els.title.textContent = "No auction scan run yet";
  els.rows.innerHTML = `<div class="empty-state">Choose a window and run the auction scan. No provider request is made until you click Run.</div>`;
  els.gradeRuns.innerHTML = "";
  els.rejectionSummary.innerHTML = "";
}

function render(data) {
  stopCountdown();
  const isLive = data.status === "live";
  els.status.textContent = isLive ? "Auction scan live" : "Blocked";
  els.status.className = `provider-pill ${isLive ? "live" : "blocked"}`;

  const rows = sortRows(data.results || []);
  const viable = rows.filter(row => row.passesCurrentBidFilters).length;
  const cachedPages = (data.gradeRuns || []).reduce((sum, run) => sum + Number(run.cachedPages || 0), 0);

  renderMetrics([
    ["Returned", number(rows.length), `${number(viable)} viable at current bid`],
    ["Window", `${number(data.appliedEndingWithinHours)} hr`, data.windowExpanded ? `expanded from ${number(data.requestedEndingWithinHours)} hr` : "requested window"],
    ["Provider", `${number(data.estimatedProviderRequests)} requests`, `${money(data.estimatedProviderCost)} est / ${number(cachedPages)} cached pages`],
    ["Parsed", `${number(data.parsedAuctionCount)} auctions`, `${number(data.auctionsInsideWindow)} in window / ${number(data.catalogMatches)} catalog matches`]
  ]);

  renderApplied(data.appliedRequest, data);
  renderGradeRuns(data.gradeRuns || []);
  renderRejections(data.gradeRuns || [], data.rejectionReasons || {});

  if (!isLive) {
    els.title.textContent = "No auction data";
    els.rows.innerHTML = `<div class="empty-state">${escapeHtml(data.errorMessage || "Auction scan is unavailable.")}</div>`;
    return;
  }

  els.title.textContent = `${rows.length} graded auctions ending within ${number(data.appliedEndingWithinHours)} hours`;
  renderRows(rows);
  startCountdown();
}

function renderApplied(request, data) {
  if (!request) {
    els.applied.innerHTML = "";
    return;
  }
  const gradeText = (request.grades || []).join(", ");
  const chips = [
    `grades ${gradeText || "psa10"}`,
    `ending <= ${request.appliedEndingWithinHours ?? data.appliedEndingWithinHours}h`,
    `market ${money(request.minMarketValue)}-${money(request.maxMarketValue)}`,
    `bid ${money(request.minCurrentBid)}-${money(request.maxCurrentBid)}`,
    `category ${escapeHtml(request.ebayCategoryId || "183454")}`,
    `condition ${escapeHtml(request.ebayConditionId || "2750")}`,
    data.windowExpanded ? `expanded: ${data.expansionReason || request.expansionReason || "explicit fallback"}` : "no fallback"
  ];
  els.applied.innerHTML = chips.map(chip => `<span>${chip}</span>`).join("");
}

function renderRows(rows) {
  if (!rows.length) {
    els.rows.innerHTML = `<div class="empty-state">No matching auctions found in the requested window. Check diagnostics before broadening the scan.</div>`;
    return;
  }

  els.rows.innerHTML = rows.map(row => {
    const listing = row.listing || {};
    const badges = buildBadges(row, listing);
    const localEnd = listing.auctionEndUtc ? new Date(listing.auctionEndUtc).toLocaleString() : "Unknown";
    const image = listing.imageUrl
      ? `<img src="${escapeAttribute(listing.imageUrl)}" alt="${escapeAttribute(row.cardName)}" loading="lazy" />`
      : `<div class="image-placeholder">No image</div>`;
    const ebayUrl = row.ebayListingUrl || listing.url;
    const priceUrl = row.priceChartingProductUrl;
    const buyNow = row.lowestComparableBuyNow == null ? row.buyNowSearchStatus : `${money(row.lowestComparableBuyNow)} / ${money(row.spreadToBuyNow)} spread`;
    const ungradedNote = row.gradedToUngradedMultiple == null ? "PriceCharting loose-price" : `${number(row.gradedToUngradedMultiple)}x graded/raw`;

    return `
      <article class="auction-card ${row.passesCurrentBidFilters ? "viable" : "review"}" data-end-utc="${escapeAttribute(listing.auctionEndUtc || "")}">
        <div class="auction-media">${image}</div>
        <div class="auction-main">
          <div class="deal-title-row">
            <strong>#${number(row.rank)} ${escapeHtml(row.cardName)}</strong>
            <span class="confidence ${confidenceClass(row.overallConfidence)}">${escapeHtml(row.overallConfidence || "Review")}</span>
          </div>
          <p class="deal-subtitle">${escapeHtml(row.setName)}${row.cardNumber ? ` / #${escapeHtml(row.cardNumber)}` : ""}${row.variantName ? ` / ${escapeHtml(row.variantName)}` : ""} / ${escapeHtml(row.gradeLabel)}</p>
          <p class="listing-title">${escapeHtml(listing.title)}</p>
          <div class="auction-badges">${badges.map(badge => `<span class="${badge.kind}">${escapeHtml(badge.text)}</span>`).join("")}</div>

          <div class="auction-kpis">
            ${kpi("Time Remaining", `<span class="countdown">${remainingText(listing.auctionEndUtc)}</span>`, localEnd)}
            ${kpi("Current Bid", money(listing.currentBid), `${number(listing.bidCount)} bids`)}
            ${kpi("Inbound Shipping", listing.inboundShipping == null ? "Unknown" : money(listing.inboundShipping), listing.inboundShipping == null ? "hard filter fails" : "separate cost")}
            ${kpi("Current All-In", listing.currentEffectiveBuyPrice == null ? "Unknown" : money(listing.currentEffectiveBuyPrice), `${number(row.percentOfMarketAtCurrentBid)}% of market`)}
            ${kpi("Market Value", money(row.priceChartingMarketValue), row.priceSourceField || row.priceSource || "PriceCharting")}
            ${kpi("Ungraded Baseline", money(row.ungradedMarketValue), ungradedNote)}
            ${kpi("Max Rational Bid", money(row.maximumRationalItemBid), `${money(row.bidHeadroom)} headroom`)}
            ${kpi("Profit", money(row.netProfitAtCurrentBid), `${number(row.netMarginPercentAtCurrentBid)}% margin`)}
            ${kpi("ROI", `${number(row.roiPercentAtCurrentBid)}%`, "at current bid")}
            ${kpi("Volume", number(row.productYearlySalesVolume), "product-level yearly")}
            ${kpi("Buy Now", buyNow || "not requested", "bounded optional lookup")}
          </div>

          <div class="review-signals">
            ${(row.failureReasons || []).map(reason => `<span class="failure">${escapeHtml(reason)}</span>`).join("")}
            ${(row.reviewSignals || []).map(reason => `<span>${escapeHtml(reason)}</span>`).join("")}
          </div>

          <div class="deal-actions">
            ${ebayUrl ? `<a class="external-button" href="${escapeAttribute(ebayUrl)}" target="_blank" rel="noreferrer">Open eBay Listing</a>` : ""}
            ${priceUrl ? `<a class="external-button secondary" href="${escapeAttribute(priceUrl)}" target="_blank" rel="noreferrer">Open PriceCharting</a>` : ""}
            <span class="match-pill">catalog ${number(row.catalogMatchScore)} / grade ${number(row.gradeMatchScore)}</span>
          </div>
        </div>
      </article>
    `;
  }).join("");
}

function buildBadges(row, listing) {
  const badges = [];
  badges.push(row.passesCurrentBidFilters ? { text: "Viable at current bid", kind: "good" } : { text: "Needs review", kind: "warn" });
  if (row.bidHeadroom < 0) badges.push({ text: "Above bid ceiling", kind: "bad" });
  if ((row.failureReasons || []).includes("MissingShipping")) badges.push({ text: "Missing shipping", kind: "bad" });
  if ((row.failureReasons || []).includes("MissingGradePrice")) badges.push({ text: "Missing grade price", kind: "bad" });
  if (listing.timeParseStatus !== "Parsed") badges.push({ text: "Time unknown", kind: "bad" });
  if (Number(listing.hoursRemainingAtCapture || 0) <= 1) badges.push({ text: "Ending soon", kind: "warn" });
  if ((row.productYearlySalesVolume || 0) > 0 && row.productYearlySalesVolume < 25) badges.push({ text: "Low volume", kind: "warn" });
  return badges;
}

function renderGradeRuns(runs) {
  if (!runs.length) {
    els.gradeRuns.innerHTML = `<div class="empty-state">No grade runs yet.</div>`;
    return;
  }

  els.gradeRuns.innerHTML = runs.map(run => `
    <details class="diagnostic-card" open>
      <summary><strong>${escapeHtml(run.gradeLabel)}</strong><span>${number(run.viableRows)} viable / ${number(run.catalogMatches)} catalog</span></summary>
      <div class="diag-lines">
        <span>${number(run.providerRequests)} provider requests / ${money(run.estimatedProviderCost)} est</span>
        <span>${number(run.listingBlocksSeen)} blocks / ${number(run.listingsParsed)} parsed / ${number(run.auctionsInsideWindow)} in window</span>
        <span>${number(run.exactGradeMatches)} exact grade matches / ${number(run.cachedPages)} cached pages / ${number(run.errorCount)} errors</span>
        ${(run.searchUrls || []).map(url => `<a href="${escapeAttribute(url)}" target="_blank" rel="noreferrer">Search URL</a>`).join("")}
      </div>
    </details>
  `).join("");
}

function renderRejections(runs, topReasons) {
  const cards = [];
  for (const [reason, count] of Object.entries(topReasons || {}).sort((a, b) => Number(b[1]) - Number(a[1]))) {
    const samples = runs.flatMap(run => (run.rejectionSamples?.[reason] || []).map(sample => ({ ...sample, grade: run.gradeLabel })));
    cards.push(renderRejection(reason, count, samples));
  }

  els.rejectionSummary.innerHTML = cards.length ? cards.join("") : `<div class="empty-state">No rejected-row diagnostics yet.</div>`;
}

function renderRejection(reason, count, samples) {
  return `
    <details class="rejection-card">
      <summary><strong>${escapeHtml(reason)}</strong><span>${number(count)}</span></summary>
      <div class="rejection-samples">
        ${samples.slice(0, 8).map(sample => `
          <div class="rejection-sample">
            ${sample.url ? `<a href="${escapeAttribute(sample.url)}" target="_blank" rel="noreferrer">${escapeHtml(sample.title || "Untitled listing")}</a>` : `<strong>${escapeHtml(sample.title || "Untitled listing")}</strong>`}
            <span>${escapeHtml(sample.grade || "")}${sample.price ? ` / ${money(sample.price)}` : ""}</span>
            <span>${(sample.reasons || []).map(escapeHtml).join("; ")}</span>
          </div>
        `).join("") || `<div class="rejection-sample"><span>No samples captured for this reason.</span></div>`}
      </div>
    </details>
  `;
}

function renderMetrics(metrics) {
  els.metrics.innerHTML = metrics.map(([label, value, note]) => `
    <div class="metric">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value ?? "-")}</strong>
      <small>${escapeHtml(note ?? "")}</small>
    </div>
  `).join("");
}

function sortRows(rows) {
  const sortBy = document.querySelector("#sortBy").value;
  return [...rows].sort((a, b) => {
    if (a.passesCurrentBidFilters !== b.passesCurrentBidFilters) return a.passesCurrentBidFilters ? -1 : 1;
    switch (sortBy) {
      case "headroom": return num(b.bidHeadroom) - num(a.bidHeadroom);
      case "profit": return num(b.netProfitAtCurrentBid) - num(a.netProfitAtCurrentBid);
      case "roi": return num(b.roiPercentAtCurrentBid) - num(a.roiPercentAtCurrentBid);
      case "effective": return num(a.listing?.currentEffectiveBuyPrice) - num(b.listing?.currentEffectiveBuyPrice);
      case "market": return num(b.priceChartingMarketValue) - num(a.priceChartingMarketValue);
      case "bids": return num(b.listing?.bidCount) - num(a.listing?.bidCount);
      case "volume": return num(b.productYearlySalesVolume) - num(a.productYearlySalesVolume);
      case "confidence": return confidenceScore(b.overallConfidence) - confidenceScore(a.overallConfidence);
      default: return dateMs(a.listing?.auctionEndUtc) - dateMs(b.listing?.auctionEndUtc);
    }
  });
}

function collectSettings() {
  const settings = {
    grades: selectedGrades(),
    query: value("query") || null,
    endingWithinHours: intValue("endingWithinHours", defaults.endingWithinHours),
    allowWindowExpansion: checked("allowWindowExpansion"),
    fallbackEndingWithinHours: intValue("fallbackEndingWithinHours", defaults.fallbackEndingWithinHours),
    pagesPerGrade: intValue("pagesPerGrade", defaults.pagesPerGrade),
    take: intValue("take", defaults.take),
    showOnlyViableAuctions: checked("showOnlyViableAuctions"),
    includeBuyNowComparison: checked("includeBuyNowComparison"),
    buyNowComparisonLimit: intValue("buyNowComparisonLimit", defaults.buyNowComparisonLimit),
    ebayCategoryId: "183454",
    ebayCategoryName: "CCG Individual Cards",
    ebayCondition: "graded",
    ebayConditionId: "2750",
    minMarketValue: decimalValue("minMarketValue", defaults.minMarketValue),
    maxMarketValue: decimalValue("maxMarketValue", defaults.maxMarketValue),
    minCurrentBid: decimalValue("minCurrentBid", defaults.minCurrentBid),
    maxCurrentBid: decimalValue("maxCurrentBid", defaults.maxCurrentBid),
    minProfit: decimalValue("minProfit", defaults.minProfit),
    minMarginPercent: decimalValue("minMarginPercent", defaults.minMarginPercent),
    minRoiPercent: decimalValue("minRoiPercent", defaults.minRoiPercent),
    minMatchScore: intValue("minMatchScore", defaults.minMatchScore),
    minProductYearlyVolume: intValue("minProductYearlyVolume", defaults.minProductYearlyVolume),
    feePercent: decimalValue("feePercent", defaults.feePercent),
    fixedFee: decimalValue("fixedFee", defaults.fixedFee),
    outboundShippingCost: decimalValue("outboundShippingCost", defaults.outboundShippingCost),
    packingCost: decimalValue("packingCost", defaults.packingCost),
    bufferCost: decimalValue("bufferCost", defaults.bufferCost)
  };
  if (!settings.grades.length) settings.grades = ["psa10"];
  return settings;
}

function saveSettings() {
  const settings = { ...collectSettings(), sortBy: value("sortBy") };
  localStorage.setItem(storageKey, JSON.stringify(settings));
}

function loadSettings() {
  let settings = defaults;
  try {
    settings = { ...defaults, ...JSON.parse(localStorage.getItem(storageKey) || "{}") };
  } catch {
    settings = defaults;
  }
  applySettings(settings);
}

function applySettings(settings) {
  for (const input of els.gradeInputs) input.checked = (settings.grades || []).includes(input.dataset.grade);
  for (const id of fields) {
    const el = document.querySelector(`#${id}`);
    if (!el) continue;
    if (el.type === "checkbox") el.checked = Boolean(settings[id]);
    else el.value = settings[id] ?? defaults[id] ?? "";
  }
  syncFallbackState();
}

function syncFallbackState() {
  const enabled = checked("allowWindowExpansion");
  const select = document.querySelector("#fallbackEndingWithinHours");
  select.disabled = !enabled;
  els.fallbackLabel.classList.toggle("disabled-control", !enabled);
}

function selectedGrades() {
  return els.gradeInputs.filter(input => input.checked).map(input => input.dataset.grade);
}

function startCountdown() {
  updateCountdowns();
  countdownTimer = window.setInterval(updateCountdowns, 1000);
}

function stopCountdown() {
  if (countdownTimer) window.clearInterval(countdownTimer);
  countdownTimer = null;
}

function updateCountdowns() {
  const now = Date.now();
  document.querySelectorAll(".auction-card[data-end-utc]").forEach(card => {
    const end = Date.parse(card.dataset.endUtc || "");
    const countdown = card.querySelector(".countdown");
    if (!Number.isFinite(end) || !countdown) return;
    const remaining = end - now;
    if (remaining <= 0) {
      countdown.textContent = "Ended";
      card.classList.add("ended");
      card.classList.remove("viable");
      return;
    }
    countdown.textContent = formatDuration(remaining);
  });
}

function remainingText(endUtc) {
  const end = Date.parse(endUtc || "");
  if (!Number.isFinite(end)) return "Unknown";
  const remaining = end - Date.now();
  return remaining <= 0 ? "Ended" : formatDuration(remaining);
}

function formatDuration(ms) {
  const totalSeconds = Math.ceil(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}

function kpi(label, value, note) {
  return `<div class="deal-kpi"><span>${escapeHtml(label)}</span><strong>${value}</strong><small>${escapeHtml(note || "")}</small></div>`;
}

function checked(id) {
  return Boolean(document.querySelector(`#${id}`)?.checked);
}

function value(id) {
  return document.querySelector(`#${id}`)?.value ?? "";
}

function decimalValue(id, fallback) {
  const parsed = Number(value(id));
  return Number.isFinite(parsed) ? parsed : fallback;
}

function intValue(id, fallback) {
  const parsed = Number.parseInt(value(id), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function num(value) {
  return Number.isFinite(Number(value)) ? Number(value) : 0;
}

function dateMs(value) {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : Number.MAX_SAFE_INTEGER;
}

function confidenceScore(value) {
  if (String(value).toLowerCase() === "high") return 3;
  if (String(value).toLowerCase() === "medium") return 2;
  if (String(value).toLowerCase() === "low") return 1;
  return 0;
}

function confidenceClass(value) {
  return String(value || "").toLowerCase();
}

function money(value) {
  if (!Number.isFinite(Number(value))) return "-";
  return Number(value).toLocaleString(undefined, { style: "currency", currency: "USD" });
}

function number(value) {
  if (!Number.isFinite(Number(value))) return "0";
  return Number(value).toLocaleString(undefined, { maximumFractionDigits: 1 });
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function escapeAttribute(value) {
  return escapeHtml(value);
}
