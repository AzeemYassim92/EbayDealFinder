const storageKey = "p2w.gradedScan.settings.v1";
const els = {
  gradeInputs: [...document.querySelectorAll("[data-grade]")],
  query: document.querySelector("#query"),
  pagesPerGrade: document.querySelector("#pagesPerGrade"),
  take: document.querySelector("#take"),
  minMarketValue: document.querySelector("#minMarketValue"),
  maxMarketValue: document.querySelector("#maxMarketValue"),
  minEffectiveBuyPrice: document.querySelector("#minEffectiveBuyPrice"),
  maxEffectiveBuyPrice: document.querySelector("#maxEffectiveBuyPrice"),
  minProfit: document.querySelector("#minProfit"),
  minMarginPercent: document.querySelector("#minMarginPercent"),
  minRoiPercent: document.querySelector("#minRoiPercent"),
  minMatchScore: document.querySelector("#minMatchScore"),
  minProductYearlyVolume: document.querySelector("#minProductYearlyVolume"),
  feePercent: document.querySelector("#feePercent"),
  fixedFee: document.querySelector("#fixedFee"),
  outboundShippingCost: document.querySelector("#outboundShippingCost"),
  packingCost: document.querySelector("#packingCost"),
  bufferCost: document.querySelector("#bufferCost"),
  sortBy: document.querySelector("#sortBy"),
  showOnlyPassingDeals: document.querySelector("#showOnlyPassingDeals"),
  includeBuyNow: document.querySelector("#includeBuyNow"),
  includeAuctions: document.querySelector("#includeAuctions"),
  runScan: document.querySelector("#runScan"),
  resetDefaults: document.querySelector("#resetDefaults"),
  status: document.querySelector("#scanStatus"),
  metrics: document.querySelector("#scanMetrics"),
  title: document.querySelector("#resultTitle"),
  applied: document.querySelector("#appliedRequest"),
  deals: document.querySelector("#dealRows"),
  gradeRuns: document.querySelector("#gradeRuns"),
  rejectionSummary: document.querySelector("#rejectionSummary")
};

let lastRows = [];
let lastPayload = null;

loadSettings();
renderReady();
els.runScan.addEventListener("click", () => runScan());
els.resetDefaults.addEventListener("click", resetDefaults);
els.sortBy.addEventListener("change", () => renderDeals(sortRows(lastRows), lastPayload));
for (const input of document.querySelectorAll("input, select")) {
  input.addEventListener("change", saveSettings);
}

async function runScan() {
  const request = buildRequest();
  saveSettings();
  setLoading(request);

  try {
    const response = await fetch("/api/scan/graded", {
      method: "POST",
      headers: { "content-type": "application/json" },
      cache: "no-store",
      body: JSON.stringify(request)
    });
    const data = await response.json();
    lastPayload = data;
    lastRows = sortRows(data.results || []);
    render(data);
  } catch (error) {
    els.status.textContent = "Scan failed";
    els.status.className = "provider-pill blocked";
    els.title.textContent = "Scan failed";
    els.deals.innerHTML = `<div class="empty-state">${escapeHtml(error.message)}</div>`;
  }
}

function buildRequest() {
  const grades = els.gradeInputs.filter((input) => input.checked).map((input) => input.dataset.grade);
  return {
    grades: grades.length ? grades : ["psa10"],
    query: blankToNull(els.query.value),
    pagesPerGrade: intValue(els.pagesPerGrade, 1),
    take: intValue(els.take, 100),
    showOnlyPassingDeals: els.showOnlyPassingDeals.checked,
    includeBuyNow: els.includeBuyNow.checked,
    includeAuctions: els.includeAuctions.checked,
    ebayCategoryId: "183454",
    ebayCategoryName: "CCG Individual Cards",
    ebayCondition: "graded",
    ebayConditionId: "2750",
    minMarketValue: numValue(els.minMarketValue, 50),
    maxMarketValue: numValue(els.maxMarketValue, 250),
    minEffectiveBuyPrice: numValue(els.minEffectiveBuyPrice, 10),
    maxEffectiveBuyPrice: numValue(els.maxEffectiveBuyPrice, 250),
    minProfit: numValue(els.minProfit, 10),
    minMarginPercent: numValue(els.minMarginPercent, 10),
    minRoiPercent: numValue(els.minRoiPercent, 10),
    minMatchScore: intValue(els.minMatchScore, 75),
    minProductYearlyVolume: intValue(els.minProductYearlyVolume, 0),
    feePercent: numValue(els.feePercent, 13.25),
    fixedFee: numValue(els.fixedFee, 0.30),
    outboundShippingCost: numValue(els.outboundShippingCost, 5),
    packingCost: numValue(els.packingCost, 1),
    bufferCost: numValue(els.bufferCost, 2)
  };
}

function renderReady() {
  renderMetrics([
    ["Estimated Cost", "$0.001/page", "Zyte broad sweep"],
    ["Default Grade", "PSA 10", "other grades explicit"],
    ["Category", "183454", "CCG Individual Cards"],
    ["Condition", "2750", "Graded"]
  ]);
  els.gradeRuns.innerHTML = `<div class="empty-state">No grade runs yet.</div>`;
  els.rejectionSummary.innerHTML = `<div class="empty-state">No rejection breakdown yet.</div>`;
}

function setLoading(request) {
  els.status.textContent = "Scanning eBay";
  els.status.className = "provider-pill";
  els.title.textContent = "Loading graded scan";
  els.applied.innerHTML = request.grades.map((grade) => `<span>${escapeHtml(grade)}</span>`).join("");
  els.deals.innerHTML = `<div class="empty-state">Fetching eBay through Zyte and matching against the local catalog...</div>`;
  els.gradeRuns.innerHTML = "";
  els.rejectionSummary.innerHTML = "";
}

function render(data) {
  const live = data.status === "live";
  els.status.textContent = live ? "Scan live" : "Blocked";
  els.status.className = `provider-pill ${live ? "live" : "blocked"}`;

  renderMetrics([
    ["Deals", number(data.dealCount), `${number(data.matchedListingCount)} matched`],
    ["Parsed", number(data.parsedListingCount), `${number(data.catalogRowsAvailable)} catalog rows`],
    ["Zyte", money(data.estimatedZyteCost), `${number(data.estimatedZyteRequests)} requests`],
    ["Grades", (data.appliedRequest?.grades || []).join(", "), `${data.appliedRequest?.ebayCategoryName || "category scoped"}`]
  ]);

  renderApplied(data.appliedRequest);
  renderDeals(sortRows(data.results || []), data);
  renderGradeRuns(data.gradeRuns || []);
  renderRejections(data.gradeRuns || []);
}

function renderApplied(request) {
  if (!request) {
    els.applied.innerHTML = "";
    return;
  }

  els.applied.innerHTML = [
    `${request.ebayCategoryName} (${request.ebayCategoryId})`,
    `${request.ebayCondition} (${request.ebayConditionId})`,
    `${number(request.pagesPerGrade)} page/grade`,
    `${percent(request.feePercent)} fee`,
    `${money(request.outboundShippingCost)} outbound`,
    request.ebayCategoryValidated ? "category validated" : "category scoped, item category not validated"
  ].map((item) => `<span>${escapeHtml(item)}</span>`).join("");
}

function renderDeals(rows, data) {
  els.title.textContent = rows.length
    ? `${rows.length} graded candidate${rows.length === 1 ? "" : "s"}`
    : "No matching graded candidates returned";

  if (!rows.length) {
    els.deals.innerHTML = `<div class="empty-state">${escapeHtml(data?.errorMessage || "No accepted listing matched the selected grades, catalog prices, and hard filters.")}</div>`;
    return;
  }

  els.deals.innerHTML = rows.map((row) => dealCard(row)).join("");
}

function dealCard(row) {
  const listing = row.listing || {};
  const match = row.catalogMatch || {};
  const card = match.card || {};
  const signals = row.reviewSignals || [];
  const grade = card.gradeLabel || "Selected grade";
  const priceBasis = card.gradeSourceField || card.gradePriceSource || "PriceCharting";

  return `
    <article class="deal-card ${row.passesHardFilters ? "passed" : "failed"}">
      <div class="deal-media">
        ${listing.imageUrl ? `<img src="${escapeAttribute(listing.imageUrl)}" alt="${escapeAttribute(card.cardName || listing.title || "listing image")}" loading="lazy" />` : `<div class="image-placeholder">No image</div>`}
      </div>
      <div class="deal-main">
        <div class="deal-title-row">
          <span class="rank">#${number(row.rank)}</span>
          <strong>${escapeHtml(card.cardName || listing.title || "Matched listing")}</strong>
          <span class="confidence ${classToken(row.confidence)}">${escapeHtml(row.confidence || "Unknown")}</span>
        </div>
        <p class="deal-subtitle">${escapeHtml(card.setName || card.priceChartingConsoleName || "Unknown set")}${card.cardNumber ? ` / #${escapeHtml(card.cardNumber)}` : ""}${card.variantName ? ` / ${escapeHtml(card.variantName)}` : ""}</p>
        <p class="listing-title">${escapeHtml(listing.title || "Untitled listing")}</p>
        <div class="deal-kpis">
          ${kpi("Grade", grade, priceBasis)}
          ${kpi("Market", money(row.expectedMarketValue), "PriceCharting selected grade")}
          ${kpi("Effective Buy", money(listing.effectivePrice), `${money(listing.listingPrice)} item${Number.isFinite(Number(listing.inboundShippingPrice)) ? ` + ${money(listing.inboundShippingPrice)} ship` : ""}`)}
          ${kpi("Total Cost", money(row.estimatedTotalCost), `${money(row.estimatedSaleFees)} fees`)}
          ${kpi("Net Profit", money(row.netProfit), `${percent(row.netMarginPercent)} margin`)}
          ${kpi("ROI", percent(row.roi), `${percent(row.underMarketPercent)} under market`)}
          ${kpi("1yr Volume", number(card.salesVolumeYearly), "product-level")}
          ${kpi("Match", number(match.score), (match.reasons || []).join(" / "))}
        </div>
        ${signals.length ? `<div class="review-signals">${signals.map((signal) => `<span>${escapeHtml(signal)}</span>`).join("")}</div>` : ""}
        <div class="deal-actions">
          ${listing.url ? `<a class="external-button" href="${escapeAttribute(listing.url)}" target="_blank" rel="noreferrer">Open eBay Listing</a>` : ""}
          ${card.priceChartingProductUrl ? `<a class="external-button secondary" href="${escapeAttribute(card.priceChartingProductUrl)}" target="_blank" rel="noreferrer">PriceCharting</a>` : ""}
        </div>
      </div>
    </article>`;
}

function renderGradeRuns(runs) {
  if (!runs.length) {
    els.gradeRuns.innerHTML = `<div class="empty-state">No grade runs returned.</div>`;
    return;
  }

  els.gradeRuns.innerHTML = runs.map((run) => `
    <details class="diagnostic-card" ${run.status === "live" ? "open" : ""}>
      <summary><strong>${escapeHtml(run.gradeLabel || run.gradeCode)}</strong><span>${escapeHtml(run.status)}</span></summary>
      <div class="request-strip">
        <span>${number(run.catalogRowsAvailable)} catalog</span>
        <span>${number(run.parsedListingCount)} parsed</span>
        <span>${number(run.matchedListingCount)} matched</span>
        <span>${number(run.dealCount)} deals</span>
        <span>${money(run.estimatedZyteCost)}</span>
      </div>
      ${(run.searchUrls || []).map((url) => `<a class="external-button secondary" href="${escapeAttribute(url)}" target="_blank" rel="noreferrer">Open eBay Search</a>`).join(" ")}
      ${run.errorMessage ? `<div class="empty-state">${escapeHtml(run.errorMessage)}</div>` : ""}
    </details>`).join("");
}

function renderRejections(runs) {
  const totals = new Map();
  const samples = new Map();
  for (const run of runs) {
    const reasons = run.stats?.broadRejectionReasons || {};
    const runSamples = run.stats?.broadRejectionSamples || {};
    for (const [reason, count] of Object.entries(reasons)) {
      totals.set(reason, (totals.get(reason) || 0) + Number(count));
      const existing = samples.get(reason) || [];
      samples.set(reason, existing.concat(runSamples[reason] || []).slice(0, 5));
    }
  }

  const entries = [...totals.entries()].sort((a, b) => b[1] - a[1]);
  if (!entries.length) {
    els.rejectionSummary.innerHTML = `<div class="empty-state">No rejected listings returned.</div>`;
    return;
  }

  els.rejectionSummary.innerHTML = entries.map(([reason, count]) => `
    <details class="rejection-card">
      <summary><strong>${escapeHtml(reason)}</strong><span>${number(count)} rows</span></summary>
      <div class="rejection-samples">
        ${(samples.get(reason) || []).map(rejectionSample).join("") || `<small>No sample titles returned.</small>`}
      </div>
    </details>`).join("");
}

function rejectionSample(sample) {
  return `
    <div class="rejection-sample">
      ${sample.url ? `<a href="${escapeAttribute(sample.url)}" target="_blank" rel="noreferrer">${escapeHtml(sample.title || "Untitled")}</a>` : `<span>${escapeHtml(sample.title || "Untitled")}</span>`}
      <span>${money(sample.listingPrice)} / ${(sample.reasons || []).map(escapeHtml).join(" / ")}</span>
    </div>`;
}

function sortRows(rows) {
  const key = els.sortBy.value;
  return [...rows].sort((a, b) => {
    if (key === "roi") return num(b.roi) - num(a.roi);
    if (key === "buy") return num(a.listing?.effectivePrice) - num(b.listing?.effectivePrice);
    if (key === "market") return num(b.expectedMarketValue) - num(a.expectedMarketValue);
    if (key === "volume") return num(b.catalogMatch?.card?.salesVolumeYearly) - num(a.catalogMatch?.card?.salesVolumeYearly);
    if (key === "confidence") return num(b.catalogMatch?.score) - num(a.catalogMatch?.score);
    return num(b.netProfit) - num(a.netProfit);
  });
}

function saveSettings() {
  const settings = buildRequest();
  settings.sortBy = els.sortBy.value;
  localStorage.setItem(storageKey, JSON.stringify(settings));
}

function loadSettings() {
  const raw = localStorage.getItem(storageKey);
  if (!raw) return;
  try {
    const settings = JSON.parse(raw);
    for (const input of els.gradeInputs) input.checked = (settings.grades || []).includes(input.dataset.grade);
    for (const [key, value] of Object.entries(settings)) {
      if (!els[key] || key === "grades") continue;
      if (els[key].type === "checkbox") els[key].checked = Boolean(value);
      else els[key].value = value ?? "";
    }
    if (settings.sortBy) els.sortBy.value = settings.sortBy;
  } catch {
    localStorage.removeItem(storageKey);
  }
}

function resetDefaults() {
  localStorage.removeItem(storageKey);
  location.reload();
}

function kpi(label, value, note) {
  return `<div class="deal-kpi"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong><small>${escapeHtml(note || "")}</small></div>`;
}

function renderMetrics(metrics) {
  els.metrics.innerHTML = metrics.map(([label, value, note]) => `<div class="metric"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value ?? "-")}</strong><small>${escapeHtml(note ?? "")}</small></div>`).join("");
}

function numValue(el, fallback) {
  const value = Number(el.value);
  return Number.isFinite(value) ? value : fallback;
}

function intValue(el, fallback) {
  const value = parseInt(el.value, 10);
  return Number.isFinite(value) ? value : fallback;
}

function num(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function blankToNull(value) {
  return String(value || "").trim() || null;
}

function money(value) {
  if (!Number.isFinite(Number(value))) return "-";
  return Number(value).toLocaleString(undefined, { style: "currency", currency: "USD" });
}

function percent(value) {
  if (!Number.isFinite(Number(value))) return "-";
  return `${Number(value).toLocaleString(undefined, { maximumFractionDigits: 1 })}%`;
}

function number(value) {
  if (!Number.isFinite(Number(value))) return "0";
  return Number(value).toLocaleString(undefined, { maximumFractionDigits: 1 });
}

function classToken(value) {
  return String(value || "").toLowerCase().replace(/[^a-z0-9]+/g, "-");
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