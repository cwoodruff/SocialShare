// Everything in this file is here because htmx cannot do it. htmx is request driven, and these
// three jobs happen entirely inside the browser with no server round trip worth making.
// Each one is labelled with why.

(function () {
  "use strict";

  // 1. Live character counter. htmx could post the textarea on every keystroke and swap a
  //    counter back, but that is a network request per character for a number the browser
  //    already knows. This counts locally and never talks to the server.
  //    The server still validates the same limit before anything publishes.
  function countCharacters(value) {
    // Count Unicode characters rather than UTF-16 units so an emoji counts once, matching
    // the server side count in RichText.CountCharacters.
    if (typeof Intl !== "undefined" && typeof Intl.Segmenter === "function") {
      return Array.from(new Intl.Segmenter(undefined, { granularity: "grapheme" }).segment(value)).length;
    }
    return Array.from(value).length;
  }

  function wireCounter(textarea) {
    var id = textarea.getAttribute("data-counter");
    var output = id ? document.getElementById(id) : null;
    if (!output) {
      return;
    }

    var limit = parseInt(output.getAttribute("data-limit"), 10);

    function update() {
      var used = countCharacters(textarea.value);
      var left = limit - used;
      output.textContent = used + " of " + limit;
      output.setAttribute("data-state", left < 0 ? "over" : left <= Math.max(10, limit * 0.1) ? "warn" : "ok");
      output.setAttribute("aria-label", left < 0
        ? Math.abs(left) + " characters over the limit"
        : left + " characters left");
    }

    textarea.addEventListener("input", update);
    update();
  }

  // 2. Image preview before upload. The file only exists in the browser until the form is
  //    submitted, so there is nothing for htmx to fetch. FileReader is the only way to show it.
  function wirePreview(input) {
    var target = document.getElementById(input.getAttribute("data-preview"));
    if (!target) {
      return;
    }

    input.addEventListener("change", function () {
      var file = input.files && input.files[0];
      if (!file) {
        target.innerHTML = "";
        return;
      }

      var reader = new FileReader();
      reader.onload = function (event) {
        target.innerHTML = "";
        var img = document.createElement("img");
        img.src = event.target.result;
        img.alt = "Preview of " + file.name;
        target.appendChild(img);

        var caption = document.createElement("p");
        caption.className = "small muted";
        caption.textContent = file.name + ", " + Math.round(file.size / 1024) + " KB. Save the post to upload it.";
        target.appendChild(caption);
      };
      reader.readAsDataURL(file);
    });
  }

  // 3. Tab switching on the compose screen. These panels are already in the DOM, so swapping
  //    them over the network would refetch content the browser is holding. This is show and
  //    hide only, and every panel stays in the one form so nothing is lost when tabs change.
  function wireTabs(root) {
    var tabs = Array.prototype.slice.call(root.querySelectorAll("[role=tab]"));

    function select(tab) {
      tabs.forEach(function (candidate) {
        var selected = candidate === tab;
        candidate.setAttribute("aria-selected", selected ? "true" : "false");
        candidate.setAttribute("tabindex", selected ? "0" : "-1");
        var panel = document.getElementById(candidate.getAttribute("aria-controls"));
        if (panel) {
          panel.hidden = !selected;
        }
      });
    }

    tabs.forEach(function (tab, index) {
      tab.addEventListener("click", function () {
        select(tab);
      });

      tab.addEventListener("keydown", function (event) {
        var next = null;
        if (event.key === "ArrowRight") { next = tabs[(index + 1) % tabs.length]; }
        if (event.key === "ArrowLeft") { next = tabs[(index - 1 + tabs.length) % tabs.length]; }
        if (event.key === "Home") { next = tabs[0]; }
        if (event.key === "End") { next = tabs[tabs.length - 1]; }
        if (next) {
          event.preventDefault();
          select(next);
          next.focus();
        }
      });
    });
  }

  // 4. The per platform toggle only needs to recolour its own tab, which is a class change on
  //    an element the browser already has. A request for that would be silly.
  function wireToggles(root) {
    root.querySelectorAll("[data-toggles-tab]").forEach(function (checkbox) {
      var tab = document.getElementById(checkbox.getAttribute("data-toggles-tab"));
      if (!tab) {
        return;
      }

      checkbox.addEventListener("change", function () {
        tab.setAttribute("data-enabled", checkbox.checked ? "true" : "false");
      });
    });
  }

  // 5. The scheduling control shows the picked time back in the user's zone. The zone comes
  //    from the profile and is rendered by the server, so this only formats what is typed.
  function wireScheduleEcho(input) {
    var echo = document.getElementById(input.getAttribute("data-echo"));
    if (!echo) {
      return;
    }

    var zone = echo.getAttribute("data-zone") || "UTC";

    function update() {
      if (!input.value) {
        echo.textContent = "";
        return;
      }

      var parsed = new Date(input.value);
      if (isNaN(parsed.getTime())) {
        echo.textContent = "";
        return;
      }

      echo.textContent = parsed.toLocaleString(undefined, {
        weekday: "short", month: "short", day: "numeric",
        hour: "numeric", minute: "2-digit"
      }) + " in " + zone;
    }

    input.addEventListener("input", update);
    update();
  }

  function wire(scope) {
    scope.querySelectorAll("textarea[data-counter]").forEach(wireCounter);
    scope.querySelectorAll("input[type=file][data-preview]").forEach(wirePreview);
    scope.querySelectorAll("[data-tabs]").forEach(wireTabs);
    scope.querySelectorAll("[data-toggle-scope]").forEach(wireToggles);
    scope.querySelectorAll("input[data-echo]").forEach(wireScheduleEcho);
  }

  document.addEventListener("DOMContentLoaded", function () {
    wire(document);

    // htmx swaps replace DOM, so anything swapped in has to be wired again. htmx:load fires
    // for every piece of new content, including out of band swaps.
    document.body.addEventListener("htmx:load", function (event) {
      var node = event.target;
      if (!node || !node.querySelectorAll) {
        return;
      }
      wire(node);
      if (node.matches && node.matches("textarea[data-counter]")) {
        wireCounter(node);
      }
    });
  });
})();
