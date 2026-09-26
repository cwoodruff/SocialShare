# 0002. htmx and Razor partials instead of a SPA

Status: Accepted

## Context

The UI has a handful of genuinely interactive moments: filtering a list, testing a connection,
uploading an image mid compose, retrying one failed platform, copying a master draft into five
bodies. None of that needs client side routing, a client side data store or a virtual DOM. All of
it needs a server round trip anyway, because the answer lives in the database or at a platform.

## Decision

htmx is the only front end library. Page handlers return Razor partials for htmx requests and
full pages otherwise. `hx-boost` is on the body so ordinary links feel like an app. Out of band
swaps handle the one case that updates several regions at once.

Plain JavaScript is allowed only where htmx genuinely cannot help, and every block of it carries
a comment saying why. The total is one file, and it does five things: the live character counter,
the image preview before upload, the compose tab switching with arrow key support, recolouring a
tab when its toggle changes, and echoing the picked schedule time back in words.

## Consequences

The good:

- No build step. No Node, no bundler, no lockfile, no dependency churn. `dotnet run` and it works.
- One place where HTML is produced, which means one place where it can be wrong.
- Every page works without JavaScript for the things that matter, because they are ordinary forms.
- The whole client side is 51 KB of htmx and 7 KB of my own script.

The bad:

- A round trip for things a SPA would do locally. For this app that is fine: the actions are
  seconds apart, not milliseconds.
- Out of band swaps need care. Swapped in content has to be rewired for the counter, which is
  why the script listens for `htmx:load` rather than only running on page load.
- Anything genuinely local, like counting characters as you type, still needs JavaScript. Doing
  it over the network would be a request per keystroke for a number the browser already has.

## What I deliberately avoided

Browser `confirm()` dialogs. htmx has `hx-confirm` and it would have been one attribute, but a
native dialog is a dead end for keyboard and screen reader users and it blocks the page. Every
destructive action swaps in an inline confirm panel instead, which is a handler and a partial.
