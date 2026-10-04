mergeInto(LibraryManager.library, {

    // ========== DIRECT PAYPAL ORDERS V2 API INTEGRATION ==========
    // No Firebase Functions required - Unity SDK → PayPal Orders v2 API direct

    // MAIN: Direct PayPal dual transaction - bypasses Firebase entirely
    UnityStartDirectPayPalTransaction: function (gameObjectNamePtr, itemNamePtr, itemDescriptionPtr, pricePtr, creatorEmailPtr, transactionIdPtr) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var itemName = UTF8ToString(itemNamePtr);
        var itemDescription = UTF8ToString(itemDescriptionPtr);
        var price = parseFloat(UTF8ToString(pricePtr));
        var creatorEmail = UTF8ToString(creatorEmailPtr);
        var transactionId = UTF8ToString(transactionIdPtr);

        console.log('🚀 DIRECT PayPal dual transaction initiated');
        console.log('💳 GameObject for callbacks:', gameObjectName);
        console.log('💰 Item:', itemName, '-', itemDescription);
        console.log('💵 Total amount:', '$' + price.toFixed(2));
        console.log('📧 Creator email:', creatorEmail);
        console.log('🆔 Transaction ID:', transactionId);

        // Store the GameObject name for SendMessage callbacks
        window.currentPayPalGameObject = gameObjectName;

        // Calculate dual transaction amounts
        var creatorAmount = Math.round(price * 0.95 * 100) / 100; // 95% to creator
        var platformAmount = Math.round(price * 0.05 * 100) / 100; // 5% to platform

        console.log('💰 Creator receives:', '$' + creatorAmount.toFixed(2), '(95%)');
        console.log('💰 Platform receives:', '$' + platformAmount.toFixed(2), '(5%)');

        // Check if direct PayPal function is available
        if (typeof window.StartDirectPayPalDualTransaction === 'function') {
            window.StartDirectPayPalDualTransaction({
                itemName: itemName,
                itemDescription: itemDescription,
                totalAmount: price,
                creatorAmount: creatorAmount,
                platformAmount: platformAmount,
                creatorEmail: creatorEmail,
                platformEmail: 'laurie@unreality3d.com',
                transactionId: transactionId,
                gameObjectName: gameObjectName
            });
        } else {
            console.warn('❌ Direct PayPal function not available');
            console.warn('🔍 Make sure PayPal SDK is loaded and direct integration is configured');

            // Send failure back to Unity
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance && window.currentPayPalGameObject) {
                window.unityInstance.SendMessage(window.currentPayPalGameObject, 'OnPaymentComplete', 'false');
            }
        }
    },

    // Test direct PayPal connection
    UnityTestDirectPayPalConnection: function (gameObjectNamePtr) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);

        console.log('🧪 Testing direct PayPal connection for GameObject:', gameObjectName);

        // Store the GameObject name
        window.currentPayPalGameObject = gameObjectName;

        // Check if PayPal SDK is available
        if (typeof paypal !== 'undefined' && paypal.Buttons) {
            console.log('✅ PayPal SDK is available');

            // Test connection
            if (typeof window.TestDirectPayPalConnection === 'function') {
                window.TestDirectPayPalConnection(gameObjectName);
            } else {
                console.log('✅ PayPal SDK available, direct integration ready');
                // Send success back to Unity
                if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                    window.unityInstance.SendMessage(gameObjectName, 'OnConnectionTestComplete', 'true');
                }
            }
        } else {
            console.log('❌ PayPal SDK not available');
            // Send failure back to Unity
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage(gameObjectName, 'OnConnectionTestComplete', 'false');
            }
        }
    },

    // ========== EXISTING PAYPAL FUNCTIONS (LEGACY - FIREBASE DEPENDENT) ==========

    UnityCallTestFunction: function () {
        if (typeof window.UnityCallTestFunction === 'function') {
            window.UnityCallTestFunction();
        } else {
            console.warn('UnityCallTestFunction not available in browser context');
        }
    },

    UnityCheckContentAccess: function (contentIdPtr) {
        var contentId = UTF8ToString(contentIdPtr);
        if (typeof window.UnityCheckContentAccess === 'function') {
            window.UnityCheckContentAccess(contentId);
        } else {
            console.warn('UnityCheckContentAccess not available in browser context');
        }
    },

    UnityRequestPayment: function (contentIdPtr, pricePtr) {
        var contentId = UTF8ToString(contentIdPtr);
        var price = UTF8ToString(pricePtr);
        if (typeof window.UnityRequestPayment === 'function') {
            window.UnityRequestPayment(contentId, price);
        } else {
            console.warn('UnityRequestPayment not available in browser context');
        }
    },

    // ========== PROFESSIONAL URL DETECTION ==========

    UnityGetCurrentURL: function () {
        var currentUrl = window.location.href;
        var bufferSize = lengthBytesUTF8(currentUrl) + 1;
        var buffer = _malloc(bufferSize);
        stringToUTF8(currentUrl, buffer, bufferSize);
        return buffer;
    },

    UnityGetDeploymentInfo: function () {
        var deploymentInfo = {
            url: window.location.href,
            hostname: window.location.hostname,
            pathname: window.location.pathname,
            isProduction: false,
            isProfessionalURL: false,
            creatorUsername: '',
            projectName: '',
            deploymentType: 'unknown'
        };

        var hostname = window.location.hostname.toLowerCase();
        var pathname = window.location.pathname;

        // NEW: Path-based professional URL detection
        if (hostname === 'unreality3d.com' && pathname.startsWith('/')) {
            var pathParts = pathname.split('/').filter(part => part.length > 0);
            if (pathParts.length >= 2) {
                deploymentInfo.isProfessionalURL = true;
                deploymentInfo.isProduction = true;
                deploymentInfo.deploymentType = 'professional';
                deploymentInfo.creatorUsername = pathParts[0];
                deploymentInfo.projectName = pathParts[1];

                console.log('Path-based professional URL detected:', deploymentInfo.creatorUsername + '/' + deploymentInfo.projectName);
            }
        }
        // LEGACY: Subdomain-based professional URL detection (backward compatibility)
        else if (hostname.endsWith('.unreality3d.com') && hostname !== 'unreality3d.com') {
            deploymentInfo.isProfessionalURL = true;
            deploymentInfo.isProduction = true;
            deploymentInfo.deploymentType = 'professional';

            var subdomain = hostname.replace('.unreality3d.com', '');
            deploymentInfo.creatorUsername = subdomain;

            var pathParts = pathname.split('/').filter(part => part.length > 0);
            if (pathParts.length > 0) {
                deploymentInfo.projectName = pathParts[0];
            }

            console.log('Legacy subdomain professional URL detected:', deploymentInfo.creatorUsername + '.unreality3d.com/' + deploymentInfo.projectName);

        } else if (hostname.includes('unreality3d.web.app') || hostname.includes('unreality3d.firebaseapp.com')) {
            deploymentInfo.isProduction = hostname.includes('unreality3d.web.app');
            deploymentInfo.deploymentType = deploymentInfo.isProduction ? 'firebase-production' : 'firebase-development';

            console.log('Firebase hosting detected:', deploymentInfo.deploymentType);

        } else if (hostname.includes('unreality3d2025.web.app') || hostname.includes('unreality3d2025.firebaseapp.com')) {
            deploymentInfo.isProduction = false;
            deploymentInfo.deploymentType = 'firebase-development';

            console.log('Development environment detected');

        } else if (hostname === 'localhost' || hostname.startsWith('192.168.') || hostname.startsWith('127.0.0.1')) {
            deploymentInfo.deploymentType = 'local';
            console.log('Local development detected');

        } else {
            deploymentInfo.deploymentType = 'unknown';
            console.log('Unknown deployment type for hostname:', hostname);
        }

        var jsonString = JSON.stringify(deploymentInfo);
        var bufferSize = lengthBytesUTF8(jsonString) + 1;
        var buffer = _malloc(bufferSize);
        stringToUTF8(jsonString, buffer, bufferSize);
        return buffer;
    },

    UnityReportDeploymentMetrics: function (deploymentTypePtr, loadTimePtr) {
        var deploymentType = UTF8ToString(deploymentTypePtr);
        var loadTime = UTF8ToString(loadTimePtr);

        console.log('Unity deployment metrics:', {
            type: deploymentType,
            loadTime: loadTime + 'ms',
            timestamp: new Date().toISOString()
        });

        if (typeof window.UnityReportAnalyticsEvent === 'function') {
            window.UnityReportAnalyticsEvent('deployment_metrics', JSON.stringify({
                deploymentType: deploymentType,
                loadTime: loadTime,
                url: window.location.href,
                timestamp: new Date().toISOString()
            }));
        }
    },

    // ========== USER PROFILE FUNCTIONS ==========

    UnityGetUserProfile: function () {
        console.log('Unity requesting user profile');

        if (typeof window.UnityGetUserProfile === 'function') {
            window.UnityGetUserProfile();
        } else {
            console.warn('UnityGetUserProfile not available in browser context');
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                var defaultProfile = {
                    userId: 'guest',
                    displayName: '',
                    userType: 'visitor',
                    paypalConnected: false,
                    creatorUsername: ''
                };
                window.unityInstance.SendMessage('U3D_FirebaseIntegration', 'OnUserProfileReceived',
                    JSON.stringify(defaultProfile));
            }
        }
    },

    UnityUpdateUserProfile: function (displayNamePtr, userTypePtr, paypalConnectedPtr) {
        var displayName = UTF8ToString(displayNamePtr);
        var userType = UTF8ToString(userTypePtr);
        var paypalConnected = UTF8ToString(paypalConnectedPtr) === 'true';

        console.log('Unity updating user profile:', displayName, userType, paypalConnected);

        if (typeof window.UnityUpdateUserProfile === 'function') {
            window.UnityUpdateUserProfile(displayName, userType, paypalConnected);
        } else {
            console.warn('UnityUpdateUserProfile not available in browser context');
        }
    },

    // ========== MULTIPLAYER SIGN-IN ==========
    // Unlike everything else in this file, the implementation ships in the build rather
    // than delegating to a window.* function supplied by a served page. No Firebase SDK
    // reaches a deployed page, so there is nothing to delegate to.
    // The callback target is passed in rather than hardcoded, so renaming the GameObject
    // cannot break it silently.

    U3DNetSignIn: function (gameObjectNamePtr, configJsonPtr) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var configJson = UTF8ToString(configJsonPtr);

        var report = function (payload) {
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage(gameObjectName, 'OnNetSignInComplete', JSON.stringify(payload));
            }
        };

        var state = window.U3DNet = window.U3DNet || {};

        var deliver = function (promise) {
            promise.then(function (uid) {
                report({ ok: true, uid: uid });
            }).catch(function (e) {
                state.signIn = null;
                report({ ok: false, message: String(e && e.message ? e.message : e) });
            });
        };

        if (state.signIn) {
            deliver(state.signIn);
            return;
        }

        var config;
        try {
            config = JSON.parse(configJson);
        } catch (e) {
            report({ ok: false, message: 'Multiplayer configuration is not valid JSON.' });
            return;
        }

        var base = 'https://www.gstatic.com/firebasejs/' + config.sdkVersion + '/';
        state.base = base;

        state.signIn = Promise.all([
            import(base + 'firebase-app.js'),
            import(base + 'firebase-auth.js')
        ]).then(function (mods) {
            var appMod = mods[0];
            var authMod = mods[1];

            // A second named app, never the default, so portal auth in this same document
            // is untouched. Persistence is a creation argument rather than a later write.
            state.app = appMod.initializeApp(config.firebase, 'u3d-net');
            state.auth = authMod.initializeAuth(state.app, {
                persistence: authMod.inMemoryPersistence
            });

            return authMod.signInAnonymously(state.auth);
        }).then(function (credential) {
            return credential.user.uid;
        });

        deliver(state.signIn);
    },
    
    // ========== BLOCK CLAIM ==========
    // Sixty-four slots under the room. A player writes into one, and the database rules
    // refuse anyone writing into an occupied one, so the refusal is the exclusion. A
    // player refused a slot draws again among the ones still free.

    U3DNetClaimBlock: function (gameObjectNamePtr, roomPathPtr, blockCount) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var roomPath = UTF8ToString(roomPathPtr);

        var report = function (payload) {
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage(gameObjectName, 'OnNetClaimComplete', JSON.stringify(payload));
            }
        };

        var state = window.U3DNet = window.U3DNet || {};

        if (!state.app || !state.auth || !state.auth.currentUser) {
            report({ ok: false, message: 'Multiplayer sign-in has not completed.' });
            return;
        }

        var uid = state.auth.currentUser.uid;

        import(state.base + 'firebase-database.js').then(function (dbMod) {
            state.dbMod = dbMod;
            state.database = dbMod.getDatabase(state.app);

            var blocksRef = dbMod.ref(state.database, roomPath + '/blocks');

            return dbMod.get(blocksRef).then(function (snapshot) {
                var taken = snapshot.exists() ? snapshot.val() : null;
                var free = [];
                for (var i = 0; i < blockCount; i++) {
                    // Test the value, not whether the key is present. A node whose keys are
                    // all whole numbers comes back as an array once more than half the
                    // indices up to the highest one are filled, with empty slots holding
                    // null — and an array owns those indices, so a key test reports every
                    // gap as taken and the room loses that capacity permanently.
                    if (!taken || taken[i] === undefined || taken[i] === null) free.push(i);
                }

                var attempt = function () {
                    if (free.length === 0) {
                        report({ ok: false, full: true });
                        return;
                    }

                    var pick = free.splice(Math.floor(Math.random() * free.length), 1)[0];
                    var slotRef = dbMod.ref(state.database, roomPath + '/blocks/' + pick);

                    // applyLocally false keeps the server timestamp from being written
                    // speculatively as a placeholder object that the retry would then read
                    // back as an occupied slot.
                    dbMod.runTransaction(slotRef, function (current) {
                        if (current !== null) return;
                        return { uid: uid, joinedAt: dbMod.serverTimestamp() };
                    }, { applyLocally: false }).then(function (result) {
                        if (!result.committed) {
                            attempt();
                            return;
                        }

                        // Registered only once the slot is ours, because the rules refuse a
                        // removal of anyone else's. This is what frees the slot when the tab
                        // closes or the browser crashes.
                        return dbMod.onDisconnect(slotRef).remove().then(function () {
                            state.slotRef = slotRef;
                            state.blockIndex = pick;
                            report({ ok: true, blockIndex: pick });
                        });
                    }).catch(function (e) {
                        report({ ok: false, message: String(e && e.message ? e.message : e) });
                    });
                };

                attempt();
            });
        }).catch(function (e) {
            report({ ok: false, message: String(e && e.message ? e.message : e) });
        });
    },

    // ========== ROSTER ==========
    // One watcher on the whole blocks node. Every delivery is the complete set of sixty-
    // four slots, so a missed update cannot leave a player permanently wrong and there is
    // no repair path to get right. The first delivery is the opening read.

    U3DNetWatchRoster: function (gameObjectNamePtr, roomPathPtr) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var roomPath = UTF8ToString(roomPathPtr);

        var report = function (payload) {
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage(gameObjectName, 'OnNetRosterUpdate', JSON.stringify(payload));
            }
        };

        var state = window.U3DNet = window.U3DNet || {};

        if (!state.dbMod || !state.database) {
            report({ ok: false, message: 'The multiplayer database is not open.' });
            return;
        }

        if (state.rosterOff) {
            state.rosterOff();
            state.rosterOff = null;
        }

        var blocksRef = state.dbMod.ref(state.database, roomPath + '/blocks');

        try {
            state.rosterOff = state.dbMod.onValue(blocksRef, function (snapshot) {
                var value = snapshot.exists() ? snapshot.val() : null;
                var slots = [];

                // Handles both shapes the node can arrive in, an object keyed by index or
                // an array with nulls in the gaps. See the note in the block claim.
                if (value) {
                    for (var key in value) {
                        if (!Object.prototype.hasOwnProperty.call(value, key)) continue;
                        var record = value[key];
                        if (!record) continue;

                        var index = parseInt(key, 10);
                        if (isNaN(index)) continue;

                        slots.push({
                            blockIndex: index,
                            uid: record.uid ? String(record.uid) : '',
                            joinedAt: typeof record.joinedAt === 'number' ? record.joinedAt : 0
                        });
                    }
                }

                report({ ok: true, slots: slots });
            }, function (e) {
                report({ ok: false, message: String(e && e.message ? e.message : e) });
            });
        } catch (e) {
            report({ ok: false, message: String(e && e.message ? e.message : e) });
        }
    },

    U3DNetLeave: function () {
        var state = window.U3DNet || {};

        if (state.rosterOff) {
            try {
                state.rosterOff();
            } catch (e) {
                console.warn('Could not detach the multiplayer roster watcher:', e);
            }
            state.rosterOff = null;
        }

        if (!state.dbMod || !state.slotRef) return;

        try {
            state.dbMod.onDisconnect(state.slotRef).cancel();
            state.dbMod.remove(state.slotRef);
        } catch (e) {
            console.warn('Could not release the multiplayer slot:', e);
        }

        state.slotRef = null;
        state.blockIndex = -1;
    },

    // A client that reconnects has lost, or is about to lose, the slot its disconnect
    // write removes, so the page reloads and joins again as a newcomer. Only once the
    // connection is back: a reload while offline loads nothing. Once per page, because
    // the roster can report the loss more than once before the connection returns.
    U3DNetReloadWhenConnected: function () {
        var state = window.U3DNet = window.U3DNet || {};

        if (state.reloadPending) return;
        state.reloadPending = true;

        if (!state.dbMod || !state.database) return;

        var connectedRef = state.dbMod.ref(state.database, '.info/connected');
        state.dbMod.onValue(connectedRef, function (snapshot) {
            if (snapshot.val() === true) window.location.reload();
        });
    },

    // ========== PEER CONNECTIONS ==========
    // The connections live here rather than in Unity, because a peer connection is a
    // browser object with no counterpart on the other side of the bridge. Unity keeps the
    // record of who is in the room and decides which side opens each connection; this opens
    // them and reports when both channels are carrying.
    //
    // U3DNetWatchSignals installs the shared machinery and runs before any offer. The
    // session guarantees that: it starts the mailbox watch as part of joining, before it
    // adds a single player.

    U3DNetConfigureIce: function (discoveryUrlPtr, relayEndpointPtr) {
        var discoveryUrl = UTF8ToString(discoveryUrlPtr);
        var relayEndpoint = UTF8ToString(relayEndpointPtr);

        var state = window.U3DNet = window.U3DNet || {};

        state.iceServers = discoveryUrl ? [{ urls: discoveryUrl }] : [];

        if (!relayEndpoint) return;
        if (!state.auth || !state.auth.currentUser) return;

        // Once for the whole session. The number of connections grows with the square of
        // the headcount and one credential covers them all. A failure leaves the discovery
        // address in place and players connect without a relay, which is a designed
        // degraded state rather than an error to make loud.
        state.auth.currentUser.getIdToken().then(function (idToken) {
            // The identity travels in the body rather than a header, so no preflight entry
            // can be missing.
            return fetch(relayEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ idToken: idToken })
            });
        }).then(function (response) {
            if (!response.ok) throw new Error('the relay refused the request with status ' + response.status);
            return response.json();
        }).then(function (payload) {
            if (!payload || !payload.iceServers || !payload.iceServers.length) return;
            // Already filtered of unreachable ports before it left the function, so the list
            // is used exactly as received and nothing here filters again.
            state.iceServers = state.iceServers.concat(payload.iceServers);
        }).catch(function (e) {
            console.warn('Players will connect without a relay: ' + (e && e.message ? e.message : e));
        });
    },

    U3DNetWatchSignals: function (gameObjectNamePtr, roomPathPtr, maxMessage) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var roomPath = UTF8ToString(roomPathPtr);

        var state = window.U3DNet = window.U3DNet || {};

        if (!state.dbMod || !state.database || !(state.blockIndex >= 0)) {
            console.warn('Cannot watch for connection offers before a block is held.');
            return;
        }

        var link = state.link = {
            room: roomPath,
            self: state.blockIndex,
            uid: state.auth && state.auth.currentUser ? state.auth.currentUser.uid : '',
            peers: {},
            busy: {},
            gatherCap: 4000,
            reliableLabel: 'u3d.reliable',
            unreliableLabel: 'u3d.unreliable',
            inboxReliable: [],
            inboxUnreliable: [],
            // Told from C# rather than held here, so there is one ceiling rather than two
            // that must be kept in step. The fallback is the same number, for the case of
            // an older build calling a newer file.
            maxMessage: maxMessage > 0 ? maxMessage : 65536,
            // The two queues are capped differently because a refusal costs different
            // things. A discarded unreliable arrival is replaced by the next one, so a
            // count is enough. A discarded reliable arrival leaves a hole nothing fills,
            // so that queue is measured in bytes and the cap is set where ordinary play
            // never reaches it: reaching it means this page has stopped reading.
            // queueOverhead stands in for the object holding each message, so a flood of
            // small messages is measured rather than counted as free.
            maxQueued: 1024,
            reliableBudget: 16777216,
            queueOverhead: 64,
            queuedReliableBytes: 0,
            warnedLong: false,
            warnedFull: false,
            warnedOverflow: false,
            sentReliable: 0,
            sentUnreliable: 0,
            recvReliable: 0,
            recvUnreliable: 0,

            // Reached when reliable arrivals have outrun this page's reading far enough
            // that the next one would have to be discarded. This page cannot repair a
            // discarded reliable message and nothing else will, so it reloads and rejoins
            // as a newcomer and arrival state rebuilds it. Only once the database
            // connection is back, because a reload while offline loads nothing.
            // state.reloadPending is the same latch U3DNetReloadWhenConnected uses, so a
            // lost block and an overflow together still reload once.
            reloadOnOverflow: function () {
                if (state.reloadPending) return;
                state.reloadPending = true;

                if (!state.dbMod || !state.database) return;

                var connectedRef = state.dbMod.ref(state.database, '.info/connected');
                state.dbMod.onValue(connectedRef, function (snapshot) {
                    if (snapshot.val() === true) window.location.reload();
                });
            }
        };

        link.report = function (block, linkState, message) {
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage(gameObjectName, 'OnNetLinkUpdate',
                    JSON.stringify({ block: block, state: linkState, message: message || '' }));
            }
        };

        link.itemRef = function (toBlock, fromBlock) {
            return state.dbMod.ref(state.database, link.room + '/signals/' + toBlock + '/' + fromBlock);
        };

        link.mailboxRef = state.dbMod.ref(state.database, roomPath + '/signals/' + link.self);

        // The whole description is written in one go rather than in pieces, so this waits
        // for the browser to finish collecting addresses. Capped, because that wait
        // includes waiting on the relay, and a relay that never answers would otherwise
        // hold the offer forever.
        link.gathered = function (pc) {
            return new Promise(function (resolve) {
                if (pc.iceGatheringState === 'complete') { resolve(); return; }

                var settled = false;
                var timer = null;

                var finish = function () {
                    if (settled) return;
                    settled = true;
                    if (timer) clearTimeout(timer);
                    pc.removeEventListener('icegatheringstatechange', check);
                    resolve();
                };

                var check = function () { if (pc.iceGatheringState === 'complete') finish(); };

                pc.addEventListener('icegatheringstatechange', check);
                timer = setTimeout(finish, link.gatherCap);
            });
        };

        link.checkOpen = function (entry) {
            if (entry.opened) return;
            if (!entry.reliable || entry.reliable.readyState !== 'open') return;
            if (!entry.unreliable || entry.unreliable.readyState !== 'open') return;

            entry.opened = true;
            link.report(entry.block, 'open', '');
        };

        link.attach = function (entry, channel) {
            // An unrecognised label is ignored, the same way an unrecognised message is,
            // which is what lets a later channel be added without disturbing this build.
            var reliable;
            var queue;
            if (channel.label === link.reliableLabel) { entry.reliable = channel; queue = link.inboxReliable; reliable = true; }
            else if (channel.label === link.unreliableLabel) { entry.unreliable = channel; queue = link.inboxUnreliable; reliable = false; }
            else return;

            // Arrivals are handed over immediately rather than as a file to be read back.
            channel.binaryType = 'arraybuffer';

            channel.onopen = function () { link.checkOpen(entry); };

            // Held until Unity drains them at the top of a fixed step rather than pushed
            // the moment they land. A push would run a component's handler at an arbitrary
            // point in Unity's frame, partway through a physics step or between two
            // components, which nothing on the far side expects.
            channel.onmessage = function (e) {
                if (!(e.data instanceof ArrayBuffer)) return;

                var bytes = new Uint8Array(e.data);

                if (bytes.length > link.maxMessage) {
                    if (!link.warnedLong) {
                        link.warnedLong = true;
                        console.warn('A multiplayer message was too long to read and was discarded.');
                    }
                    return;
                }

                if (reliable) {
                    // Every peer's reliable channel pushes into this one queue, so the
                    // measurement covers what is waiting here from everybody.
                    var cost = bytes.length + link.queueOverhead;

                    if (link.queuedReliableBytes + cost > link.reliableBudget) {
                        if (!link.warnedOverflow) {
                            link.warnedOverflow = true;
                            console.warn('Multiplayer messages are arriving faster than they are being read. This page will rejoin the room.');
                        }
                        // Nothing past the budget is kept. The page is reloading, and
                        // holding more is the growth the budget exists to stop.
                        link.reloadOnOverflow();
                        return;
                    }

                    link.queuedReliableBytes += cost;
                    queue.push({ block: entry.block, bytes: bytes });
                    link.recvReliable += bytes.length;
                    return;
                }

                if (queue.length >= link.maxQueued) {
                    if (!link.warnedFull) {
                        link.warnedFull = true;
                        console.warn('Multiplayer position updates are arriving faster than they are being read, so some are being discarded.');
                    }
                    return;
                }

                queue.push({ block: entry.block, bytes: bytes });
                link.recvUnreliable += bytes.length;
            };

            // A channel handed to the answering side can already be open by the time it
            // arrives, in which case the event above never fires.
            link.checkOpen(entry);
        };

        link.drop = function (block) {
            var entry = link.peers[block];
            if (!entry) return;

            delete link.peers[block];
            // Cleared with the connection. A block left marked busy is skipped by consume
            // for the life of the page, so every later offer from that block is read and
            // ignored with nothing said.
            link.busy[block] = false;
            try { entry.pc.close(); } catch (e) { }
        };

        link.make = function (block) {
            link.drop(block);

            // A page opened with ?forceRelay in its address makes relay-only connections, so
            // any connection it completes went through the relay by construction. It is the
            // instrument for whether the relay carries traffic, which no ordinary test can
            // answer where both machines can reach each other directly.
            var config = { iceServers: state.iceServers || [] };
            if (/(^|[?&])forceRelay(=|&|$)/.test(window.location.search)) config.iceTransportPolicy = 'relay';

            var pc = new RTCPeerConnection(config);
            var entry = { block: block, pc: pc, reliable: null, unreliable: null, opened: false, offerSdp: null };
            link.peers[block] = entry;

            pc.onconnectionstatechange = function () {
                if (pc.connectionState === 'failed') {
                    link.report(block, 'failed', 'the connection could not be established');
                }
            };

            pc.ondatachannel = function (e) { link.attach(entry, e.channel); };

            return entry;
        };

        link.answer = function (fromBlock, record) {
            var existing = link.peers[fromBlock];

            // Already connected to this player, so this is an offer re-sent after the
            // connection came up.
            if (existing && existing.opened) {
                state.dbMod.remove(link.itemRef(link.self, fromBlock));
                return;
            }

            // The same offer again, re-sent because the far side has not connected yet. It is
            // answered by re-writing the answer this connection already made, never by
            // building a second connection: the far side applies only the first answer it
            // receives, so a rebuild leaves it checking routes against a connection that no
            // longer exists here while the new one waits for checks that never come. The
            // offering side keeps the same rule for the same reason.
            if (existing && existing.remoteOfferSdp === record.sdp) {
                state.dbMod.remove(link.itemRef(link.self, fromBlock));

                // Still gathering, so its own chain will write the answer.
                if (!existing.answerSdp) return;

                state.dbMod.set(link.itemRef(fromBlock, link.self), {
                    toUid: record.fromUid,
                    fromUid: link.uid,
                    type: 'answer',
                    sdp: existing.answerSdp
                }).catch(function (e) {
                    link.report(fromBlock, 'failed', String(e && e.message ? e.message : e));
                });
                return;
            }

            link.busy[fromBlock] = true;
            link.report(fromBlock, 'answering', '');

            var entry = link.make(fromBlock);
            link.busy[fromBlock] = true;

            // Kept so a re-send of this same offer is recognised as one.
            entry.remoteOfferSdp = record.sdp;

            // The offer is consumed here rather than after the answer is written. It has
            // been read, so it has done its job, and leaving it in place until a later step
            // succeeds means any failure at all leaves it sitting there — where the next
            // mailbox delivery reads it again, answers again, fails again, and tears down
            // whatever connection this block holds each time round.
            state.dbMod.remove(link.itemRef(link.self, fromBlock));

            entry.pc.setRemoteDescription({ type: 'offer', sdp: record.sdp }).then(function () {
                return entry.pc.createAnswer();
            }).then(function (answer) {
                return entry.pc.setLocalDescription(answer);
            }).then(function () {
                return link.gathered(entry.pc);
            }).then(function () {
                // Kept so a re-sent offer can be answered again without rebuilding.
                entry.answerSdp = entry.pc.localDescription.sdp;

                return state.dbMod.set(link.itemRef(fromBlock, link.self), {
                    toUid: record.fromUid,
                    fromUid: link.uid,
                    type: 'answer',
                    sdp: entry.answerSdp
                });
            }).then(function () {
                link.busy[fromBlock] = false;
                link.sweep();
            }).catch(function (e) {
                link.busy[fromBlock] = false;

                // The connection goes with the failure. A block is re-issued to whoever
                // joins next, so a half-built connection left under this number would be
                // inherited by a different person.
                link.drop(fromBlock);

                link.report(fromBlock, 'failed', String(e && e.message ? e.message : e));
                link.sweep();
            });
        };

        link.accept = function (fromBlock, record) {
            state.dbMod.remove(link.itemRef(link.self, fromBlock));

            var entry = link.peers[fromBlock];
            if (!entry || entry.opened) return;

            // An answer is only meaningful while this side is still waiting on one. A second
            // copy of the same answer would otherwise be rejected and read as a failure.
            if (entry.pc.signalingState !== 'have-local-offer') return;

            entry.pc.setRemoteDescription({ type: 'answer', sdp: record.sdp }).catch(function (e) {
                link.report(fromBlock, 'failed', String(e && e.message ? e.message : e));
            });
        };

        link.consume = function (fromBlock, record) {
            if (link.busy[fromBlock]) return;

            // Left behind for whoever held this block before. Every item names its intended
            // recipient, so it can be told apart and deleted on sight, which is what removes
            // the need to clear the whole mailbox on claiming a block.
            if (!record || record.toUid !== link.uid) {
                state.dbMod.remove(link.itemRef(link.self, fromBlock));
                return;
            }

            if (record.type === 'offer') link.answer(fromBlock, record);
            else if (record.type === 'answer') link.accept(fromBlock, record);
        };

        // The whole mailbox rather than only what is new, because a re-sent offer replaces
        // the item at its own path and would not read as new. Both shapes are handled,
        // named entries or a list with holes in it, for the reason in the block claim.
        link.deliverMailbox = function (value) {
            if (!value) return;

            for (var key in value) {
                if (!Object.prototype.hasOwnProperty.call(value, key)) continue;

                var record = value[key];
                if (!record) continue;

                var from = parseInt(key, 10);
                if (isNaN(from) || from === link.self) continue;

                link.consume(from, record);
            }
        };

        // Reads the mailbox once, on demand. The watcher below fires on change, so an item
        // that arrived while this block was mid-handshake was skipped by consume and never
        // looked at again — nothing writes to that path a second time. This is run when a
        // handshake finishes, which is the moment the skipped item becomes answerable.
        // Read fresh rather than replayed from the last delivery, so an item deleted in the
        // meantime is not acted on.
        link.sweep = function () {
            state.dbMod.get(link.mailboxRef).then(function (snapshot) {
                if (!snapshot.exists()) return;
                link.deliverMailbox(snapshot.val());
            }).catch(function (e) {
                console.warn('The connection mailbox could not be re-read:', e);
            });
        };

        if (state.signalsOff) {
            try { state.signalsOff(); } catch (e) { }
            state.signalsOff = null;
        }

        try {
            state.signalsOff = state.dbMod.onValue(link.mailboxRef, function (snapshot) {
                link.deliverMailbox(snapshot.exists() ? snapshot.val() : null);
            }, function (e) {
                console.warn('The connection mailbox could not be read:', e);
            });
        } catch (e) {
            console.warn('The connection mailbox could not be watched:', e);
        }
    },

    U3DNetOffer: function (gameObjectNamePtr, roomPathPtr, remoteBlock, remoteUidPtr) {
        var roomPath = UTF8ToString(roomPathPtr);
        var remoteUid = UTF8ToString(remoteUidPtr);

        var state = window.U3DNet || {};
        var link = state.link;

        if (!link) {
            console.warn('Cannot open a connection before the connection mailbox is watched.');
            return;
        }

        var existing = link.peers[remoteBlock];
        if (existing && existing.opened) return;

        // A re-send re-writes the offer this connection already made. It does not build a
        // new one, and that is the whole point: link.make closes whatever connection this
        // block holds, so a re-send used to tear down the connection the far side was in
        // the middle of answering — the answer then arrived for a connection that no
        // longer existed and was discarded, with nothing said on either machine.
        if (existing) {
            // The first offer has not been written yet, so its own chain will write it when
            // address collection finishes. Writing a description without addresses in it
            // would replace the real offer with an unusable one.
            if (!existing.offerSdp) return;

            state.dbMod.set(link.itemRef(remoteBlock, link.self), {
                toUid: remoteUid,
                fromUid: link.uid,
                type: 'offer',
                sdp: existing.offerSdp
            }).catch(function (e) {
                link.report(remoteBlock, 'failed', String(e && e.message ? e.message : e));
            });
            return;
        }

        var entry = link.make(remoteBlock);

        // Both channels exist before the offer is made. An offer built with no channels has
        // nothing to carry them, and adding one afterwards would need a second round of
        // offer and answer that a mailbox written once in each direction has no room for.
        link.attach(entry, entry.pc.createDataChannel(link.reliableLabel));
        link.attach(entry, entry.pc.createDataChannel(link.unreliableLabel, {
            ordered: false,
            maxRetransmits: 0
        }));

        entry.pc.createOffer().then(function (offer) {
            return entry.pc.setLocalDescription(offer);
        }).then(function () {
            return link.gathered(entry.pc);
        }).then(function () {
            // Kept so a re-send has something to re-write without rebuilding.
            entry.offerSdp = entry.pc.localDescription.sdp;

            // Keyed by both blocks, so a re-send replaces the previous offer rather than
            // adding a second one, and a stalled handshake cannot pile up.
            return state.dbMod.set(link.itemRef(remoteBlock, link.self), {
                toUid: remoteUid,
                fromUid: link.uid,
                type: 'offer',
                sdp: entry.offerSdp
            });
        }).catch(function (e) {
            link.report(remoteBlock, 'failed', String(e && e.message ? e.message : e));
        });
    },

    U3DNetSend: function (remoteBlock, channel, bytesPtr, length) {
        var state = window.U3DNet || {};
        var link = state.link;
        if (!link) return;

        // Refused here rather than at the far end. An over-long message is discarded by
        // whoever receives it, so without this the one machine that could fix the problem
        // is the one machine that never hears about it.
        if (length > link.maxMessage) {
            if (!link.warnedLongSend) {
                link.warnedLongSend = true;
                console.warn('A multiplayer message was too long to send and was discarded.');
            }
            return;
        }

        var entry = link.peers[remoteBlock];
        if (!entry) return;

        // 0 is the reliable channel and 1 the unreliable one, matching NetChannel.
        var target = channel === 0 ? entry.reliable : entry.unreliable;
        if (!target || target.readyState !== 'open') return;

        try {
            // send copies what it is given before returning, and nothing can grow the
            // module's memory in the middle of this call, so the view is handed over
            // directly rather than copied first.
            target.send(HEAPU8.subarray(bytesPtr, bytesPtr + length));

            if (channel === 0) link.sentReliable += length;
            else link.sentUnreliable += length;
        } catch (e) {
            // The channel closed between Unity's record of it being open and this call.
            // Ordinary, and the roster reports the departure on its own.
        }
    },

    U3DNetReceive: function (bufferPtr, maxLength, senderPtr) {
        var state = window.U3DNet || {};
        var link = state.link;
        if (!link) return 0;

        // Reliable first, so within one step nothing on the unreliable channel can
        // overtake a message that had to arrive in order.
        var fromReliable = link.inboxReliable.length > 0;
        var item = fromReliable ? link.inboxReliable.shift() : link.inboxUnreliable.shift();
        if (!item) return 0;

        // Off the reliable queue, so its share of the budget is returned whatever happens
        // to it below.
        if (fromReliable) link.queuedReliableBytes -= item.bytes.length + link.queueOverhead;

        // Longer than the buffer Unity supplied, which can only happen if the two sides
        // disagree about the ceiling. Reported as its own answer rather than as zero,
        // because zero means nothing is left and would stall everything behind it.
        if (item.bytes.length > maxLength) return -1;

        HEAPU8.set(item.bytes, bufferPtr);
        HEAP32[senderPtr >> 2] = item.block;
        return item.bytes.length;
    },

    U3DNetClosePeer: function (remoteBlock) {
        var state = window.U3DNet || {};
        if (state.link) state.link.drop(remoteBlock);
    },

    U3DNetCloseAll: function () {
        var state = window.U3DNet || {};

        if (state.signalsOff) {
            try { state.signalsOff(); } catch (e) { }
            state.signalsOff = null;
        }

        if (!state.link) return;

        for (var block in state.link.peers) {
            if (!Object.prototype.hasOwnProperty.call(state.link.peers, block)) continue;
            try { state.link.peers[block].pc.close(); } catch (e) { }
        }

        state.link.peers = {};
        state.link.inboxReliable = [];
        state.link.inboxUnreliable = [];
        state.link = null;
    },

    // ========== NETWORKING STATUS FUNCTIONS ==========

    UnityReportNetworkStatus: function (statusPtr, playerCountPtr) {
        var status = UTF8ToString(statusPtr);
        var playerCount = UTF8ToString(playerCountPtr);

        console.log('Unity network status update:', status, 'players:', playerCount);

        if (typeof window.UnityReportNetworkStatus === 'function') {
            window.UnityReportNetworkStatus(status, playerCount);
        }
    },

    UnityReportPlayerJoined: function (playerNamePtr, userTypePtr) {
        var playerName = UTF8ToString(playerNamePtr);
        var userType = UTF8ToString(userTypePtr);

        console.log('Unity player joined:', playerName, 'type:', userType);

        if (typeof window.UnityReportPlayerJoined === 'function') {
            window.UnityReportPlayerJoined(playerName, userType);
        }
    },

    UnityReportPlayerLeft: function (playerNamePtr) {
        var playerName = UTF8ToString(playerNamePtr);

        console.log('Unity player left:', playerName);

        if (typeof window.UnityReportPlayerLeft === 'function') {
            window.UnityReportPlayerLeft(playerName);
        }
    },

    // ========== ANALYTICS AND TELEMETRY ==========

    UnityReportAnalyticsEvent: function (eventNamePtr, eventDataPtr) {
        var eventName = UTF8ToString(eventNamePtr);
        var eventData = UTF8ToString(eventDataPtr);

        console.log('Unity analytics event:', eventName, eventData);

        if (typeof window.UnityReportAnalyticsEvent === 'function') {
            window.UnityReportAnalyticsEvent(eventName, eventData);
        }
    },

    UnityReportPerformanceMetrics: function (fpsPtr, memoryUsagePtr, networkLatencyPtr) {
        var fps = UTF8ToString(fpsPtr);
        var memoryUsage = UTF8ToString(memoryUsagePtr);
        var networkLatency = UTF8ToString(networkLatencyPtr);

        if (typeof window.UnityReportPerformanceMetrics === 'function') {
            window.UnityReportPerformanceMetrics(fps, memoryUsage, networkLatency);
        }
    },

    // ========== BROWSER INTEGRATION FUNCTIONS ==========

    UnityRequestFullscreen: function () {
        console.log('Unity requesting fullscreen');

        if (typeof window.UnityRequestFullscreen === 'function') {
            window.UnityRequestFullscreen();
        } else {
            if (document.documentElement.requestFullscreen) {
                document.documentElement.requestFullscreen();
            } else if (document.documentElement.webkitRequestFullscreen) {
                document.documentElement.webkitRequestFullscreen();
            } else if (document.documentElement.msRequestFullscreen) {
                document.documentElement.msRequestFullscreen();
            }
        }
    },

    UnityExitFullscreen: function () {
        console.log('Unity exiting fullscreen');

        if (typeof window.UnityExitFullscreen === 'function') {
            window.UnityExitFullscreen();
        } else {
            if (document.exitFullscreen) {
                document.exitFullscreen();
            } else if (document.webkitExitFullscreen) {
                document.webkitExitFullscreen();
            } else if (document.msExitFullscreen) {
                document.msExitFullscreen();
            }
        }
    },

    UnityGetBrowserInfo: function () {
        var browserInfo = {
            userAgent: navigator.userAgent,
            platform: navigator.platform,
            language: navigator.language,
            cookieEnabled: navigator.cookieEnabled,
            onLine: navigator.onLine,
            screenWidth: screen.width,
            screenHeight: screen.height,
            windowWidth: window.innerWidth,
            windowHeight: window.innerHeight,
            pixelRatio: window.devicePixelRatio || 1
        };

        console.log('Unity browser info:', browserInfo);

        if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
            window.unityInstance.SendMessage('U3D_FirebaseIntegration', 'OnBrowserInfoReceived',
                JSON.stringify(browserInfo));
        }
    },

    // ========== ERROR HANDLING AND DEBUGGING ==========

    UnityReportError: function (errorMessagePtr, stackTracePtr) {
        var errorMessage = UTF8ToString(errorMessagePtr);
        var stackTrace = UTF8ToString(stackTracePtr);

        console.error('Unity error reported:', errorMessage);
        console.error('Stack trace:', stackTrace);

        if (typeof window.UnityReportError === 'function') {
            window.UnityReportError(errorMessage, stackTrace);
        }

        if (typeof window.UnityReportAnalyticsEvent === 'function') {
            window.UnityReportAnalyticsEvent('unity_error', JSON.stringify({
                message: errorMessage,
                stackTrace: stackTrace,
                timestamp: new Date().toISOString()
            }));
        }
    },

    UnityLog: function (levelPtr, messagePtr) {
        var level = UTF8ToString(levelPtr);
        var message = UTF8ToString(messagePtr);

        switch (level.toLowerCase()) {
            case 'error':
                console.error('[Unity]', message);
                break;
            case 'warning':
                console.warn('[Unity]', message);
                break;
            case 'info':
                console.info('[Unity]', message);
                break;
            default:
                console.log('[Unity]', message);
                break;
        }

        if (typeof window.UnityLog === 'function') {
            window.UnityLog(level, message);
        }
    },

    // ========== UTILITY FUNCTIONS ==========

    UnityGetTimestamp: function () {
        return Date.now();
    },

    UnityGetRandomGUID: function () {
        var guid = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0;
            var v = c == 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });

        var bufferSize = lengthBytesUTF8(guid) + 1;
        var buffer = _malloc(bufferSize);
        stringToUTF8(guid, buffer, bufferSize);
        return buffer;
    },

    UnitySetLocalStorage: function (keyPtr, valuePtr) {
        var key = UTF8ToString(keyPtr);
        var value = UTF8ToString(valuePtr);

        try {
            localStorage.setItem('U3D_' + key, value);
            return 1;
        } catch (e) {
            console.warn('Failed to set localStorage:', e);
            return 0;
        }
    },

    UnityGetLocalStorage: function (keyPtr) {
        var key = UTF8ToString(keyPtr);

        try {
            var value = localStorage.getItem('U3D_' + key) || '';
            var bufferSize = lengthBytesUTF8(value) + 1;
            var buffer = _malloc(bufferSize);
            stringToUTF8(value, buffer, bufferSize);
            return buffer;
        } catch (e) {
            console.warn('Failed to get localStorage:', e);
            var bufferSize = 1;
            var buffer = _malloc(bufferSize);
            stringToUTF8('', buffer, bufferSize);
            return buffer;
        }
    },

    UnityRemoveLocalStorage: function (keyPtr) {
        var key = UTF8ToString(keyPtr);

        try {
            localStorage.removeItem('U3D_' + key);
            return 1;
        } catch (e) {
            console.warn('Failed to remove localStorage:', e);
            return 0;
        }
    },

    // ========== LEGACY DUAL TRANSACTION FUNCTIONS (FIREBASE DEPENDENT) ==========
    // These are kept for backward compatibility but marked as deprecated

    currentPayPalGameObject: '',

    // DEPRECATED: Firebase Functions-based dual transaction
    UnityStartDualTransactionWithGameObject: function (gameObjectNamePtr, itemNamePtr, itemDescriptionPtr, pricePtr, transactionIdPtr) {
        console.warn('⚠️ UnityStartDualTransactionWithGameObject is DEPRECATED');
        console.warn('⚠️ This method requires Firebase Functions authentication');
        console.warn('⚠️ Use UnityStartDirectPayPalTransaction for direct PayPal integration');

        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var itemName = UTF8ToString(itemNamePtr);
        var itemDescription = UTF8ToString(itemDescriptionPtr);
        var price = UTF8ToString(pricePtr);
        var transactionId = UTF8ToString(transactionIdPtr);

        window.currentPayPalGameObject = gameObjectName;

        console.log('Unity dual transaction (DEPRECATED Firebase method):', {
            itemName: itemName,
            itemDescription: itemDescription,
            price: price,
            transactionId: transactionId
        });

        if (typeof window.UnityStartDualTransaction === 'function') {
            window.UnityStartDualTransaction(itemName, itemDescription, price, transactionId);
        } else {
            console.warn('Firebase-based UnityStartDualTransaction not available');
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance && window.currentPayPalGameObject) {
                window.unityInstance.SendMessage(window.currentPayPalGameObject, 'OnPaymentComplete', 'false');
            }
        }
    },

    // DEPRECATED: Firebase Functions-based authentication check
    UnityCheckAuthenticationStatusWithGameObject: function (gameObjectNamePtr) {
        console.warn('⚠️ UnityCheckAuthenticationStatusWithGameObject is DEPRECATED');
        console.warn('⚠️ Direct PayPal integration does not require authentication');

        var gameObjectName = UTF8ToString(gameObjectNamePtr);

        console.log('Unity authentication check (DEPRECATED) for GameObject:', gameObjectName);

        window.currentPayPalGameObject = gameObjectName;

        if (typeof window.UnityCheckAuthenticationStatus === 'function') {
            window.UnityCheckAuthenticationStatus();
        } else {
            console.log('No auth required for direct PayPal - returning success');
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance && window.currentPayPalGameObject) {
                window.unityInstance.SendMessage(window.currentPayPalGameObject, 'OnAuthenticationChecked', 'true');
            }
        }
    },

    // ULTRA-DEPRECATED: Original methods without GameObject name support
    UnityStartDualTransaction: function (itemNamePtr, itemDescriptionPtr, pricePtr, transactionIdPtr) {
        console.warn('⚠️ UnityStartDualTransaction (without GameObject name) is ULTRA-DEPRECATED');
        console.warn('⚠️ Will cause "SendMessage: object not found!" errors');
        console.warn('⚠️ Use UnityStartDirectPayPalTransaction instead');

        var itemName = UTF8ToString(itemNamePtr);
        var itemDescription = UTF8ToString(itemDescriptionPtr);
        var price = UTF8ToString(pricePtr);
        var transactionId = UTF8ToString(transactionIdPtr);

        console.log('Unity dual transaction (ULTRA-DEPRECATED method):', {
            itemName: itemName,
            itemDescription: itemDescription,
            price: price,
            transactionId: transactionId
        });

        if (typeof window.UnityStartDualTransaction === 'function') {
            window.UnityStartDualTransaction(itemName, itemDescription, price, transactionId);
        } else {
            console.warn('Firebase-based UnityStartDualTransaction not available');
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage('PayPalDualTransaction', 'OnPaymentComplete', 'false');
            }
        }
    },

    UnityCheckAuthenticationStatus: function () {
        console.warn('⚠️ UnityCheckAuthenticationStatus (without GameObject name) is ULTRA-DEPRECATED');
        console.warn('⚠️ Will cause "SendMessage: object not found!" errors');

        console.log('Unity authentication check (ULTRA-DEPRECATED method)');

        if (typeof window.UnityCheckAuthenticationStatus === 'function') {
            window.UnityCheckAuthenticationStatus();
        } else {
            console.log('No auth required for direct PayPal - returning success');
            if (typeof window.unityInstance !== 'undefined' && window.unityInstance) {
                window.unityInstance.SendMessage('PayPalDualTransaction', 'OnAuthenticationChecked', 'true');
            }
        }
    }
});