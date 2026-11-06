mergeInto(LibraryManager.library, {
    U3D_IsTouchPrimaryDevice: function () {
        var ua = (navigator && navigator.userAgent) ? navigator.userAgent : '';
        if (/OculusBrowser|PicoBrowser|\bQuest\b/.test(ua)) return 0;
        return (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) ? 1 : 0;
    },

    U3D_GetCssPixelRatio: function () {
        var canvas = Module['canvas'];
        if (!canvas || !canvas.clientWidth) return 1.0;
        var zoom = (window.visualViewport && window.visualViewport.scale > 0) ? window.visualViewport.scale : 1.0;
        return (canvas.width / canvas.clientWidth) / zoom;
    },

    U3D_GetSafeAreaTopCss: function () {
        var probe = document.getElementById('u3d-safe-area-probe');
        if (!probe) {
            probe = document.createElement('div');
            probe.id = 'u3d-safe-area-probe';
            probe.style.cssText = 'position:fixed;top:0;left:0;width:0;height:0;visibility:hidden;pointer-events:none;padding-top:env(safe-area-inset-top);';
            document.body.appendChild(probe);
        }
        var value = parseFloat(window.getComputedStyle(probe).paddingTop);
        return isNaN(value) ? 0.0 : value;
    }
});
