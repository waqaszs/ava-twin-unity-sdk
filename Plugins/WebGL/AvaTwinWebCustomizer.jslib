mergeInto(LibraryManager.library, {
  AvaTwin_SetIframeMessageTarget: function (gameObjectNamePtr, methodNamePtr, allowedOriginPtr) {
    var gameObjectName = UTF8ToString(gameObjectNamePtr);
    var methodName = UTF8ToString(methodNamePtr);
    var allowedOrigin = UTF8ToString(allowedOriginPtr);

    if (!window.__avaTwinWebCustomizerBridge) {
      window.__avaTwinWebCustomizerBridge = {
        gameObjectName: "",
        methodName: "",
        allowedOrigin: "",
        debugDummyEnabled: false,
        debugDummyUrl: "",
        listenerAttached: false
      };
    }

    var bridge = window.__avaTwinWebCustomizerBridge;
    bridge.gameObjectName = gameObjectName;
    bridge.methodName = methodName;
    bridge.allowedOrigin = allowedOrigin;

    if (!bridge.listenerAttached) {
      window.addEventListener("message", function (event) {
        var overlay = document.getElementById("ava-twin-customizer-overlay");
        var iframe = document.getElementById("ava-twin-customizer-iframe");
        if (!overlay || !iframe) return;
        if (!iframe.contentWindow || event.source !== iframe.contentWindow) return;

        var currentBridge = window.__avaTwinWebCustomizerBridge;
        if (!currentBridge) return;

        if (currentBridge.allowedOrigin && currentBridge.allowedOrigin !== "*" && event.origin !== currentBridge.allowedOrigin) {
          return;
        }

        var urlPayload = "";
        if (typeof event.data === "string") {
          urlPayload = event.data;
        } else if (typeof event.data === "object" && event.data !== null) {
          // Handle structured Ava-Twin messages
          if (event.data.type === "ava-twin:avatar-saved" && event.data.glb_url) {
            urlPayload = JSON.stringify({ glb_url: event.data.glb_url, skin_tone: event.data.skin_tone || "", avatar_id: event.data.avatar_id || "" });
            // Also close the iframe overlay after avatar is saved
            var overlayEl = document.getElementById("ava-twin-customizer-overlay");
            if (overlayEl && overlayEl.parentNode) {
              overlayEl.parentNode.removeChild(overlayEl);
            }
          } else if (event.data.type === "ava-twin:customizer-ready") {
            // Customizer loaded — no action needed on Unity side
            return;
          } else {
            try {
              urlPayload = JSON.stringify(event.data);
            } catch (e) {
              urlPayload = "";
            }
          }
        }

        if (!urlPayload) return;

        if (typeof SendMessage === "function") {
          SendMessage(currentBridge.gameObjectName, currentBridge.methodName, urlPayload);
          return;
        }

        if (typeof unityInstance !== "undefined" && unityInstance && typeof unityInstance.SendMessage === "function") {
          unityInstance.SendMessage(currentBridge.gameObjectName, currentBridge.methodName, urlPayload);
        }
      });

      bridge.listenerAttached = true;
    }
  },

  AvaTwin_SetIframeDebugDummy: function (enabled, dummyUrlPtr) {
    if (!window.__avaTwinWebCustomizerBridge) {
      window.__avaTwinWebCustomizerBridge = {
        gameObjectName: "",
        methodName: "",
        allowedOrigin: "",
        debugDummyEnabled: false,
        debugDummyUrl: "",
        listenerAttached: false
      };
    }

    var bridge = window.__avaTwinWebCustomizerBridge;
    bridge.debugDummyEnabled = !!enabled;
    bridge.debugDummyUrl = UTF8ToString(dummyUrlPtr);
  },

  AvaTwin_OpenFullscreenIframe: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var overlayId = "ava-twin-customizer-overlay";
    var iframeId = "ava-twin-customizer-iframe";
    var closeBtnId = "ava-twin-customizer-close-btn";
    var debugButtonId = "ava-twin-customizer-debug-return-btn";

    var sendToUnity = function (urlPayload) {
      if (!urlPayload) return;

      var bridge = window.__avaTwinWebCustomizerBridge;
      if (!bridge || !bridge.gameObjectName || !bridge.methodName) return;

      if (typeof SendMessage === "function") {
        SendMessage(bridge.gameObjectName, bridge.methodName, urlPayload);
        return;
      }

      if (typeof unityInstance !== "undefined" && unityInstance && typeof unityInstance.SendMessage === "function") {
        unityInstance.SendMessage(bridge.gameObjectName, bridge.methodName, urlPayload);
      }
    };

    var existingOverlay = document.getElementById(overlayId);
    if (existingOverlay) {
      var existingIframe = document.getElementById(iframeId);
      if (existingIframe && existingIframe.src !== url) {
        existingIframe.src = url;
      }

      // Close button removed — players must save to proceed.

      var existingDebugButton = document.getElementById(debugButtonId);
      var bridgeForExisting = window.__avaTwinWebCustomizerBridge;
      if (bridgeForExisting && bridgeForExisting.debugDummyEnabled) {
        if (!existingDebugButton) {
          existingDebugButton = document.createElement("button");
          existingDebugButton.id = debugButtonId;
          existingDebugButton.textContent = "Return Dummy URL";
          existingDebugButton.style.position = "absolute";
          existingDebugButton.style.top = "56px";
          existingDebugButton.style.right = "12px";
          existingDebugButton.style.zIndex = "2147483646";
          existingDebugButton.style.padding = "10px 14px";
          existingDebugButton.style.border = "0";
          existingDebugButton.style.borderRadius = "6px";
          existingDebugButton.style.background = "#2563eb";
          existingDebugButton.style.color = "#fff";
          existingDebugButton.style.cursor = "pointer";
          existingDebugButton.onclick = function () {
            var bridge = window.__avaTwinWebCustomizerBridge;
            var dummyUrl = bridge && bridge.debugDummyUrl ? bridge.debugDummyUrl : "https://customizer.ava-twin.me/debug-dummy.glb";
            sendToUnity(dummyUrl);
            if (existingOverlay && existingOverlay.parentNode) {
              existingOverlay.parentNode.removeChild(existingOverlay);
            }
          };
          existingOverlay.appendChild(existingDebugButton);
        } else {
          existingDebugButton.style.display = "block";
        }
      } else if (existingDebugButton) {
        existingDebugButton.style.display = "none";
      }

      existingOverlay.style.display = "block";
      return;
    }

    var overlay = document.createElement("div");
    overlay.id = overlayId;
    overlay.style.position = "fixed";
    overlay.style.top = "0";
    overlay.style.left = "0";
    overlay.style.width = "100vw";
    overlay.style.height = "100vh";
    overlay.style.zIndex = "2147483647";
    overlay.style.background = "#000";

    var iframe = document.createElement("iframe");
    iframe.id = iframeId;
    iframe.src = url;
    iframe.style.width = "100%";
    iframe.style.height = "100%";
    iframe.style.border = "0";
    iframe.setAttribute("allow", "camera; microphone; fullscreen; clipboard-read; clipboard-write");
    iframe.setAttribute("allowfullscreen", "true");
    // Remove sandbox to allow same-origin cookie access for auth
    iframe.removeAttribute("sandbox");

    // After the iframe loads, send an init message so the customizer captures our origin.
    iframe.onload = function () {
      try {
        iframe.contentWindow.postMessage({ type: "ava-twin:init" }, "*");
      } catch (e) {
        // Non-fatal — iframe may have restrictive CSP.
      }
    };

    overlay.appendChild(iframe);

    // Close button removed — players must save an avatar to proceed.

    var bridge = window.__avaTwinWebCustomizerBridge;
    if (bridge && bridge.debugDummyEnabled) {
      var debugButton = document.createElement("button");
      debugButton.id = debugButtonId;
      debugButton.textContent = "Return Dummy URL";
      debugButton.style.position = "absolute";
      debugButton.style.top = "56px";
      debugButton.style.right = "12px";
      debugButton.style.zIndex = "2147483646";
      debugButton.style.padding = "10px 14px";
      debugButton.style.border = "0";
      debugButton.style.borderRadius = "6px";
      debugButton.style.background = "#2563eb";
      debugButton.style.color = "#fff";
      debugButton.style.cursor = "pointer";
      debugButton.onclick = function () {
        var currentBridge = window.__avaTwinWebCustomizerBridge;
        var dummyUrl = currentBridge && currentBridge.debugDummyUrl ? currentBridge.debugDummyUrl : "https://customizer.ava-twin.me/debug-dummy.glb";
        sendToUnity(dummyUrl);
        if (overlay && overlay.parentNode) {
          overlay.parentNode.removeChild(overlay);
        }
      };
      overlay.appendChild(debugButton);
    }

    document.body.appendChild(overlay);
  },

  AvaTwin_CloseFullscreenIframe: function () {
    var overlay = document.getElementById("ava-twin-customizer-overlay");
    if (overlay && overlay.parentNode) {
      overlay.parentNode.removeChild(overlay);
    }
  },
});
