const steps = [
  { id: "welcome", label: "欢迎", meta: "WELCOME" },
  { id: "version", label: "版本", meta: "VERSION" },
  { id: "check", label: "安装检查", meta: "CHECK" },
  { id: "options", label: "安装选项", meta: "OPTIONS" },
  { id: "installing", label: "安装", meta: "INSTALL" },
  { id: "complete", label: "完成", meta: "DONE" }
];

let pageIndex = 0;
let installingTimer = null;
let checkTimers = [];

const pages = [...document.querySelectorAll(".page")];
const nextButton = document.querySelector("#nextButton");
const backButton = document.querySelector("#backButton");

function showPage(index) {
  index = Math.max(0, Math.min(steps.length - 1, index));
  const previous = pages[pageIndex];
  previous.classList.add("is-leaving");
  previous.classList.remove("is-active");
  window.setTimeout(() => previous.classList.remove("is-leaving"), 380);

  pageIndex = index;
  const current = pages[pageIndex];
  current.classList.add("is-active");
  updateFooter();

  if (steps[pageIndex].id === "check") playChecks();
  else clearCheckTimers();
  if (steps[pageIndex].id === "options") checkInstallPath();
  if (steps[pageIndex].id === "installing") playInstallation();
  else stopInstallation();
}

function updateFooter() {
  const id = steps[pageIndex].id;
  backButton.style.visibility = pageIndex === 0 || id === "installing" ? "hidden" : "visible";
  nextButton.disabled = false;

  if (id === "options") nextButton.innerHTML = "<span>开始安装</span><i>→</i>";
  else if (id === "installing") nextButton.style.visibility = "hidden";
  else if (id === "complete") nextButton.innerHTML = "<span>启动小猫助手</span><i>→</i>";
  else nextButton.innerHTML = "<span>继续</span><i>→</i>";
  if (id !== "installing") nextButton.style.visibility = "visible";
}

function clearCheckTimers() {
  checkTimers.forEach(window.clearTimeout);
  checkTimers = [];
}

function playChecks() {
  clearCheckTimers();
  const items = [...document.querySelectorAll(".check-item")];
  const summary = document.querySelector(".check-summary");
  items.forEach(item => item.classList.remove("is-checked"));
  summary.classList.remove("is-visible");
  items.forEach((item, index) => {
    checkTimers.push(window.setTimeout(() => item.classList.add("is-checked"), 260 + index * 340));
  });
  checkTimers.push(window.setTimeout(() => summary.classList.add("is-visible"), 260 + items.length * 340));
}

function stopInstallation() {
  if (installingTimer) window.clearInterval(installingTimer);
  installingTimer = null;
}

function playInstallation() {
  stopInstallation();
  let progress = 0;
  const progressValue = document.querySelector("#progressValue");
  const progressBar = document.querySelector("#progressBar");
  const installStatus = document.querySelector("#installStatus");
  const messages = ["正在验证安装资源…", "正在写入程序文件…", "正在创建快捷方式…", "正在完成安装检查…"];

  const paint = () => {
    const stage = Math.min(3, Math.floor(progress / 25));
    progressValue.textContent = `${progress}%`;
    progressBar.style.width = `${progress}%`;
    installStatus.textContent = messages[stage];
  };
  paint();
  installingTimer = window.setInterval(() => {
    progress = Math.min(100, progress + 1);
    paint();
    if (progress === 100) {
      stopInstallation();
      window.setTimeout(() => showPage(5), 650);
    }
  }, 42);
}

nextButton.addEventListener("click", () => {
  if (pageIndex === steps.length - 1) showPage(0);
  else showPage(pageIndex + 1);
});
backButton.addEventListener("click", () => showPage(pageIndex - 1));
const folderDialog = document.querySelector("#folderDialog");
const folderBasePath = document.querySelector("#folderBasePath");
const folderResultPreview = document.querySelector("#folderResultPreview");
const installPath = document.querySelector("#installPath");
const locationStatus = document.querySelector("#locationStatus");
let locationCheckTimer = null;

function checkInstallPath() {
  const usable = /^[a-zA-Z]:\\[^<>:"|?*]+/.test(installPath.value.trim());
  locationStatus.textContent = usable
    ? "位置可用 · 磁盘剩余 128.6 GB，需要至少 0.9 GB"
    : "安装位置不可用，请输入完整路径";
  locationStatus.classList.toggle("is-error", !usable);
  if (steps[pageIndex].id === "options") nextButton.disabled = !usable;
}

installPath.addEventListener("input", () => {
  window.clearTimeout(locationCheckTimer);
  locationStatus.textContent = "正在检查安装位置…";
  locationStatus.classList.remove("is-error");
  nextButton.disabled = true;
  locationCheckTimer = window.setTimeout(checkInstallPath, 250);
});

function appendProductFolder(value) {
  const base = value.trim().replace(/[\\/]+$/, "");
  return /[\\/]CatAssistant$/i.test(base) ? base : `${base}\\CatAssistant`;
}

function updateFolderPreview() {
  folderResultPreview.textContent = appendProductFolder(folderBasePath.value);
}

document.querySelector("#browseButton").addEventListener("click", () => {
  const current = installPath.value;
  const withoutProduct = current.replace(/[\\/]CatAssistant[\\/]?$/i, "");
  folderBasePath.value = withoutProduct;
  updateFolderPreview();
  folderDialog.showModal();
});
folderBasePath.addEventListener("input", updateFolderPreview);
document.querySelector("#confirmFolderButton").addEventListener("click", () => {
  installPath.value = appendProductFolder(folderBasePath.value);
  checkInstallPath();
});

const requestedPage = new URLSearchParams(window.location.search).get("page");
const requestedIndex = steps.findIndex(step => step.id === requestedPage);
if (requestedIndex > 0) showPage(requestedIndex);
else updateFooter();
