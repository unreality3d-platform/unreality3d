mergeInto(LibraryManager.library, {
    U3D_IsTouchPrimaryDevice: function () {
        return (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) ? 1 : 0;
    }
});
