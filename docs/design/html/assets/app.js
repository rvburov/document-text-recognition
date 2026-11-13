/* Макет «Распознавание текста документов»: рамка окна Windows, панель разделов, значки, интерактивность.
   Доски описывают только содержимое; поведение задается атрибутами (см. README.md). */
(function () {
  "use strict";

  var BOARDS = [
    { file: "01-main.html", n: 1, title: "Обработка", group: "Основные экраны", sub: "Главный экран: папки, запуск, результат" },
    { file: "02-run.html", n: 2, title: "Идет распознавание", group: "Основные экраны", sub: "Ход работы, счетчики, остановка" },
    { file: "03-results.html", n: 3, title: "Результат", group: "Основные экраны", sub: "Таблица файлов, поиск, статус, карточка файла" },
    { file: "04-templates.html", n: 4, title: "Шаблоны", group: "Основные экраны", sub: "Поиск, список, сведения и страница шаблона" },
    { file: "05-settings.html", n: 5, title: "Настройки", group: "Основные экраны", sub: "Распознавание, имя файла, вид, обновление" },
    { file: "06-about.html", n: 6, title: "О программе и обновление", group: "Основные экраны", sub: "Версия, проверка и установка обновления" },
    { file: "07-first-run.html", n: 7, title: "Первый запуск", group: "Сценарии и состояния", sub: "Пустые папки, подсказки по шагам" },
    { file: "08-errors.html", n: 8, title: "Ошибки", group: "Сценарии и состояния", sub: "Что видит человек и что делать" },
    { file: "09-dialogs.html", n: 9, title: "Диалоги", group: "Сценарии и состояния", sub: "Подтверждения" },
    { file: "10-install.html", n: 10, title: "Установка и обновление", group: "Сценарии и состояния", sub: "Установщик, ярлык, обновление при запуске" },
    { file: "11-ds-foundations.html", n: 11, title: "Дизайн-система: основы", group: "Дизайн-система", sub: "Цвета, шрифты, размеры, значки" },
    { file: "12-ds-components.html", n: 12, title: "Дизайн-система: компоненты", group: "Дизайн-система", sub: "Кнопки, поля, таблица, диалоги" }
  ];
  var NAV = [
    { id: "rename", label: "Обработка", icon: "ScanText", file: "01-main.html" },
    { id: "templates", label: "Шаблоны", icon: "Template", file: "04-templates.html", count: "11" },
    { id: "settings", label: "Настройки", icon: "Settings", file: "05-settings.html" }
  ];
  var VERSION = "1.0.0";
  var params = new URLSearchParams(location.search);
  var inBoards = /\/boards\//.test(location.pathname);
  var root = inBoards ? "../" : "";
  var store = {
    get: function (k) { try { return localStorage.getItem("pr-" + k); } catch (e) { return null; } },
    set: function (k, v) { try { localStorage.setItem("pr-" + k, v); } catch (e) { /* без хранилища */ } }
  };

  /* ── Значки ── */
  function icon(name, size, stroke) {
    var filled = (window.PR_FILL || {})[name];
    if (filled) {
      return '<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" stroke="none"><path d="' + filled + '"/></svg>';
    }
    var d = (window.PR_ICONS || {})[name];
    if (!d) return "";
    size = 16;
    stroke = 1.9;
    return '<svg width="' + size + '" height="' + size + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="' +
      stroke + '" stroke-linecap="round" stroke-linejoin="round"><path d="' + d + '"/></svg>';
  }
  // Галочка флажка как в программе резервного копирования (AppStyle._draw_checkbox): квадрат 15,
  // ломаная (0,24; 0,53) → (0,43; 0,71) → (0,77; 0,31) от стороны, толщина 15 / 8, скругленные концы.
  var CHECK = '<svg width="15" height="15" viewBox="0 0 15 15" fill="none" stroke="currentColor" stroke-width="1.875" ' +
    'stroke-linecap="round" stroke-linejoin="round"><path d="M3.6 7.95L6.45 10.65L11.55 4.65"/></svg>';
  function renderIcons(scope) {
    scope.querySelectorAll("i[data-icon]").forEach(function (el) {
      el.innerHTML = icon(el.dataset.icon, +el.dataset.size || 16, +el.dataset.stroke || 0);
    });
    scope.querySelectorAll(".check .box").forEach(function (box) { box.innerHTML = CHECK; });
    // Кнопка со значком и текстом: текст ровно по центру, значок слева от него (справа такой же пустой отступ).
    scope.querySelectorAll(".btn").forEach(function (btn) {
      var text = Array.prototype.some.call(btn.childNodes, function (n) { return n.nodeType === 3 && n.textContent.trim(); });
      btn.classList.toggle("with-icon", !!btn.querySelector("i[data-icon]") && text);
    });
  }

  /* ── Окно ── */
  function navHtml(active) {
    var items = NAV.map(function (n) {
      return '<a class="item' + (n.id === active ? " current" : "") + '" href="' + n.file + '"' + (n.isNew ? " data-new" : "") + '>' +
        icon(n.icon, 16) + "<span>" + n.label + "</span>" + (n.count ? '<span class="count">' + n.count + "</span>" : "") + "</a>";
    }).join("");
    return '<nav class="nav">' + items +
      '<div class="bottom"><a class="item' + (active === "about" ? " current" : "") + '" href="06-about.html">' + icon("Info", 16) +
      "<span>О программе</span></a><div class=\"version\">Версия " + VERSION + "</div></div></nav>";
  }
  function logo(size) {
    return '<svg class="logo" width="' + size + '" height="' + size + '" viewBox="0 0 24 24"><rect x="3" y="2" width="15" height="20" rx="2" fill="var(--pr-bg)" stroke="var(--pr-text)" stroke-width="1.4"/>' +
      '<path d="M7 8h7M7 12h5" stroke="var(--pr-text)" stroke-width="1.4" stroke-linecap="round"/><circle cx="17" cy="17" r="5" fill="var(--pr-accent)"/>' +
      '<path d="M14.8 17.1l1.5 1.5 2.9-3" stroke="var(--pr-text-on-accent)" stroke-width="1.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  }
  function frame(screen) {
    var bare = screen.hasAttribute("data-bare");
    var title = screen.dataset.title || "Распознавание текста документов";
    var win = document.createElement("div");
    win.className = "win" + (bare ? " dialog-window" : "");
    if (screen.dataset.width) win.style.width = screen.dataset.width + "px";
    if (screen.dataset.height) win.style.height = screen.dataset.height + "px";
    win.innerHTML = '<div class="win-title">' + logo(16) + '<span class="name">' + title + "</span>" +
      (bare ? "" : '<span class="ctl">' + icon("Minimize", 12, 1.5) + '</span><span class="ctl">' + icon("Maximize", 11, 1.5) + "</span>") +
      '<span class="ctl close">' + icon("Close", 14, 1.5) + "</span></div>";
    var body = document.createElement("div");
    body.className = "win-body";
    if (!bare) body.insertAdjacentHTML("beforeend", navHtml(screen.dataset.active || "rename"));
    var content = document.createElement("div");
    content.className = "content";
    while (screen.firstChild) content.appendChild(screen.firstChild);
    body.appendChild(content);
    win.appendChild(body);
    win.insertAdjacentHTML("beforeend", '<div class="scrim"></div><div class="toast"></div>');
    content.querySelectorAll(".dialog").forEach(function (d) { win.appendChild(d); });
    // Заголовок раздела прокручивается вместе с содержимым, а не стоит над ним.
    var head = content.querySelector(":scope > .page-head"), page = content.querySelector(":scope > .page");
    if (head && page) page.insertBefore(head, page.firstChild);
    screen.appendChild(win);
    if (screen.dataset.caption) screen.insertAdjacentHTML("afterbegin", '<p class="pr-caption">' + screen.dataset.caption + "</p>");
    return win;
  }

  /* ── Панель вверху доски ── */
  function toolbar() {
    var file = location.pathname.split("/").pop();
    var i = BOARDS.findIndex(function (b) { return b.file === file; });
    var bar = document.createElement("div");
    bar.className = "pr-toolbar";
    var options = BOARDS.map(function (b) {
      return '<option value="' + b.file + '"' + (b.file === file ? " selected" : "") + ">" + b.n + " · " + b.title + "</option>";
    }).join("");
    bar.innerHTML = '<a class="home" href="' + root + 'index.html">Распознавание текста документов · макет</a>' +
      (i >= 0 ? '<select aria-label="Доска">' + options + "</select>" +
        (i > 0 ? '<a class="step" href="' + BOARDS[i - 1].file + '">← ' + BOARDS[i - 1].title + "</a>" : "") +
        (i < BOARDS.length - 1 ? '<a class="step" href="' + BOARDS[i + 1].file + '">' + BOARDS[i + 1].title + " →</a>" : "") : "") +
      '<span class="spacer"></span><label class="check' + (document.body.classList.contains("hide-new") ? "" : " on") + '" data-toggle-new><span class="box">' +
      icon("Check", 12, 2.5) + "</span>Метки «предложение»</label>" +
      '<span class="zoom">Масштаб <button data-zoom="1">100%</button><button data-zoom="1.25">125%</button><button data-zoom="1.5">150%</button></span>';
    document.body.prepend(bar);
    var select = bar.querySelector("select");
    if (select) select.addEventListener("change", function () { location.href = select.value; });
  }
  function applyZoom(z) {
    document.querySelectorAll(".pr-screens").forEach(function (s) { s.style.zoom = z; });
    document.querySelectorAll("[data-zoom]").forEach(function (b) { b.classList.toggle("current", b.dataset.zoom === String(z)); });
  }

  /* ── Слои ── */
  function winOf(el) { return el.closest(".win") || document; }
  function closeAll(win) {
    win.querySelectorAll(".scrim, .dialog, .popup").forEach(function (el) { el.classList.remove("is-open"); });
  }
  function open(win, id) {
    var el = win.querySelector("#" + id) || document.getElementById(id);
    if (!el) return;
    if (el.classList.contains("popup")) { win.querySelectorAll(".popup").forEach(function (p) { if (p !== el) p.classList.remove("is-open"); }); el.classList.toggle("is-open"); return; }
    closeAll(win);
    var scrim = win.querySelector(".scrim");
    if (scrim) scrim.classList.add("is-open");
    el.classList.add("is-open");
  }
  function toast(win, text) {
    var t = win.querySelector(".toast");
    if (!t) return;
    t.textContent = text;
    t.classList.add("is-open");
    clearTimeout(t._timer);
    t._timer = setTimeout(function () { t.classList.remove("is-open"); }, 1900);
  }

  /* ── Имитация прогона ── */
  function setRun(win, state) {
    win.dataset.run = state;
    win.querySelectorAll("[data-run-show]").forEach(function (el) {
      el.hidden = el.dataset.runShow.split(/\s+/).indexOf(state) < 0;
    });
    win.querySelectorAll("[data-run-enable]").forEach(function (el) {
      el.disabled = el.dataset.runEnable.split(/\s+/).indexOf(state) < 0;
    });
  }
  function startRun(win) {
    var total = +(win.dataset.total || 248);
    var done = 0;
    var rows = Array.prototype.slice.call(win.querySelectorAll("[data-run-row]"));
    rows.forEach(function (r) { r.setAttribute("data-pending", ""); });
    setRun(win, "running");
    // Ход работы и таблица на виду: раздел прокручивается так, чтобы карточка хода работы была вверху.
    // Таблица при этом дотягивается до низа окна (не ниже 300), строки прокручиваются внутри нее.
    var page = win.querySelector(".page.fill"), card = win.querySelector("[data-run-card]");
    var fill = win.querySelector(".results .table-fill");
    if (page && card) {
      if (fill) fill.style.minHeight = Math.max(300, page.clientHeight - (fill.offsetTop - card.offsetTop + 12) - 16) + "px";
      page.scrollTo({ top: Math.max(0, card.offsetTop - 12), behavior: "smooth" });
    }
    clearInterval(win._timer);
    win._timer = setInterval(function () {
      done = Math.min(total, done + Math.ceil(total / 24));
      var part = done / total;
      win.querySelectorAll(".progress > span").forEach(function (b) { b.style.width = Math.round(part * 100) + "%"; });
      win.querySelectorAll("[data-counter]").forEach(function (c) {
        var max = +c.dataset.max || 0;
        c.textContent = c.dataset.counter === "found" ? total : Math.round(max * part);
      });
      win.querySelectorAll("[data-percent]").forEach(function (p) { p.textContent = Math.round(part * 100) + "%"; });
      var visible = Math.round(rows.length * part);
      rows.forEach(function (r, k) { r.toggleAttribute("data-pending", k >= visible); });
      if (done >= total) { clearInterval(win._timer); setRun(win, "done"); toast(win, "Готово: переименовано " + (win.querySelector('[data-counter="renamed"]') || {}).textContent + " из " + total); }
    }, 220);
  }
  function stopRun(win) { clearInterval(win._timer); setRun(win, "stopped"); toast(win, "Остановлено: файлы в работе дочитаны и не переименованы"); }

  /* ── Щелчки ── */
  function onClick(e) {
    var el = e.target.closest("[data-zoom]");
    if (el) { store.set("zoom", el.dataset.zoom); applyZoom(+el.dataset.zoom); return; }
    el = e.target.closest("[data-toggle-new]");
    if (el) { el.classList.toggle("on"); document.body.classList.toggle("hide-new", !el.classList.contains("on")); store.set("new", el.classList.contains("on") ? "1" : "0"); return; }
    el = e.target.closest("[data-nav]");
    if (el) { location.href = el.dataset.nav; return; }
    var win = winOf(e.target);
    el = e.target.closest("[data-close]");
    if (el || e.target.classList.contains("scrim")) { closeAll(win); return; }
    el = e.target.closest(".popup .option");
    if (el) {
      var pop = el.closest(".popup");
      pop.querySelectorAll(".option").forEach(function (o) { o.classList.toggle("current", o === el); });
      var owner = win.querySelector('[data-open="' + pop.id + '"] [data-select-label]');
      if (owner) owner.textContent = el.textContent;
      pop.classList.remove("is-open");
      if (el.dataset.goto) goto(win, el.dataset.goto);
      if (el.dataset.filter !== undefined) filterRows(win, el.dataset.filter);
      if (el.dataset.toast) toast(win, el.dataset.toast);
      return;
    }
    el = e.target.closest("[data-open]");
    if (el) { open(win, el.dataset.open); return; }
    el = e.target.closest("[data-run-start]");
    if (el) { closeAll(win); startRun(win); return; }
    el = e.target.closest("[data-run-stop]");
    if (el) { closeAll(win); stopRun(win); return; }
    el = e.target.closest(".check:not([data-toggle-new])");
    if (el) { el.classList.toggle("on"); return; }
    el = e.target.closest(".switch");
    if (el) { el.classList.toggle("on"); return; }
    el = e.target.closest(".segment");
    if (el) {
      el.parentElement.querySelectorAll(".segment").forEach(function (s) { s.classList.toggle("current", s === el); });
      if (el.dataset.goto) goto(win, el.dataset.goto);
      return;
    }
    el = e.target.closest(".tab");
    if (el) {
      var tabs = el.parentElement;
      tabs.querySelectorAll(".tab").forEach(function (t) { t.classList.toggle("current", t === el); });
      if (el.dataset.panel) win.querySelectorAll("[data-tab-panel]").forEach(function (p) { p.hidden = p.dataset.tabPanel !== el.dataset.panel; });
      if (el.dataset.filter !== undefined) filterRows(win, el.dataset.filter);
      return;
    }
    el = e.target.closest("[data-goto]");
    if (el) { goto(win, el.dataset.goto); if (el.dataset.toast) toast(win, el.dataset.toast); return; }
    el = e.target.closest("[data-file]");
    if (el) { toast(win, "Открывается в программе просмотра PDF: " + el.dataset.file); return; }
    el = e.target.closest("tr[data-detail], .li[data-detail]");
    if (el) { select(win, el); return; }
    el = e.target.closest("[data-spin]");
    if (el) { el.classList.add("spin"); setTimeout(function () { el.classList.remove("spin"); }, 1500); }
    el = e.target.closest("[data-remove]");
    if (el) { win.querySelectorAll(el.dataset.remove).forEach(function (r) { r.hidden = true; }); }
    el = e.target.closest("[data-toast]");
    if (el) { closeAll(win); toast(win, el.dataset.toast); }
  }
  function goto(win, page) {
    win.querySelectorAll("[data-page]").forEach(function (p) { p.hidden = p.dataset.page !== page; });
    closeAll(win);
  }
  // Строка видна, если подходит и статус из списка, и строка поиска (по именам «Было» и «Стало»).
  function applyFilters(win) {
    var status = win.dataset.filter || "";
    var query = (win.dataset.query || "").trim().toLowerCase();
    win.querySelectorAll("tr[data-status]").forEach(function (r) {
      var names = (r.cells[0].textContent + " " + r.cells[2].textContent).toLowerCase();
      r.hidden = (!!status && r.dataset.status !== status) || (!!query && names.indexOf(query) < 0);
    });
    // Выбранный файл скрыт отбором: карточка переходит на первый видимый файл (или пустеет).
    var host = win.querySelector("[data-detail-host]");
    var sel = win.querySelector("tr.selected");
    if (host && (!sel || sel.hidden)) {
      var first = win.querySelector("tr[data-status]:not([hidden])");
      if (first) select(win, first);
      else { if (sel) sel.classList.remove("selected"); host.innerHTML = '<div class="card file-card empty">Нет файлов по этому отбору.</div>'; }
    }
  }
  // Поиск по списку (шаблоны): по имени файла; если выбранный скрыт, выбирается первый видимый.
  function filterList(win, q) {
    q = q.trim().toLowerCase();
    win.querySelectorAll(".li[data-detail]").forEach(function (li) {
      var name = (li.querySelector(".li-name") || li).textContent.toLowerCase();
      li.hidden = !!q && name.indexOf(q) < 0;
    });
    var cur = win.querySelector(".li.current");
    if (!cur || cur.hidden) {
      var first = win.querySelector(".li[data-detail]:not([hidden])");
      if (first) select(win, first);
    }
  }
  function filterRows(win, status) { win.dataset.filter = status; applyFilters(win); }
  function select(win, row) {
    var list = row.closest("tbody, .list");
    list.querySelectorAll(".selected, .current").forEach(function (r) { r.classList.remove("selected", "current"); });
    row.classList.add(row.tagName === "TR" ? "selected" : "current");
    if (showCard(win, row)) return;
    win.querySelectorAll("[data-detail-panel]").forEach(function (p) { p.hidden = p.dataset.detailPanel !== row.dataset.detail; });
  }

  /* ── Карточка файла справа от таблицы: статус, наименование документа, причина ── */
  function esc(t) { return String(t).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;"); }
  function kv(k, v, cls, stacked) {
    return '<div class="kv' + (stacked ? " stacked" : "") + '"><span class="k">' + k + '</span><span class="v' + (cls ? " " + cls : "") + '">' + v + "</span></div>";
  }
  function fileCard(row) {
    var kind = row.dataset.status;
    var named = kind === "done" || kind === "same";       // новое имя есть, если документ распознан
    var stalo = row.cells[2].textContent;                  // новое имя или причина пропуска
    var link = row.querySelector("[data-file]");
    return '<div class="card file-card">' +
      '<div class="file-title"><i data-icon="FileText"></i><span>' + esc(row.cells[0].textContent) + "</span></div>" +
      kv("Статус", row.cells[3].innerHTML) +
      kv("Наименование документа", named ? esc(stalo) : "нет", "", true) +
      (named ? "" : kv("Причина", esc(stalo), kind === "error" ? "t-danger" : "", true)) +
      '<div class="file-actions"><button class="btn" data-file="' + esc(link ? link.dataset.file : "") + '">Открыть PDF</button>' +
      '<button class="btn" data-toast="Папка открыта в Проводнике, файл выделен">Показать в папке</button></div>' +
      (named ? "" : '<button class="btn full" data-new data-open="manual">Ввести имя вручную…</button>') +
      "</div>";
  }
  function showCard(win, row) {
    var host = win.querySelector("[data-detail-host]");
    if (!host || row.tagName !== "TR") return false;
    host.innerHTML = fileCard(row);
    renderIcons(host);
    return true;
  }

  /* ── Галерея ── */
  function gallery(host) {
    var html = "", group = "";
    BOARDS.forEach(function (b) {
      if (b.group !== group) {
        if (group) html += "</div>";
        group = b.group;
        html += "<h2>" + group + '</h2><div class="grid">';
      }
      html += '<a class="thumb" href="boards/' + b.file + '"><div class="frame-box"><iframe loading="lazy" src="boards/' + b.file +
        '?thumb=1" tabindex="-1" title="' + b.title + '"></iframe></div><div class="cap">' + b.n + " · " + b.title + '</div><div class="sub">' + b.sub + "</div></a>";
    });
    host.innerHTML = '<div class="gallery">' + html + "</div></div>";
  }

  document.addEventListener("DOMContentLoaded", function () {
    if (params.get("thumb")) document.body.classList.add("thumbnail");
    if (store.get("new") === "0") document.body.classList.add("hide-new");
    document.querySelectorAll(".pr-screen").forEach(function (s) {
      var win = frame(s);
      if (s.dataset.run) setRun(win, s.dataset.run);
      if (s.dataset.total) win.dataset.total = s.dataset.total;
    });
    document.querySelectorAll("[data-detail-host]").forEach(function (h) {
      var win = winOf(h), row = win.querySelector("tr.selected");
      if (row) showCard(win, row);
    });
    var host = document.querySelector("[data-gallery-host]");
    if (host) gallery(host);
    if (!params.get("thumb")) toolbar();
    renderIcons(document);
    applyZoom(+(store.get("zoom") || 1));
    document.addEventListener("click", onClick);
    document.addEventListener("input", function (e) {
      var lf = e.target.closest("input[data-search-list]");
      if (lf) { filterList(winOf(lf), lf.value); return; }
      var field = e.target.closest("input[data-search]");
      if (!field) return;
      var win = winOf(field);
      win.dataset.query = field.value;
      applyFilters(win);
    });
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") document.querySelectorAll(".win").forEach(closeAll);
    });
  });
  window.PR = { icon: icon, boards: BOARDS };
})();
