(function () {
    'use strict';

    const $ = (sel) => document.querySelector(sel);
    const chatMessages = $('#chat-messages');
    const chatInput = $('#chat-input');
    const btnSend = $('#btn-send');
    const btnStop = $('#btn-stop');
    const btnSettings = $('#btn-settings');
    const btnContext = $('#btn-context');
    const btnSave = $('#btn-save-settings');
    const btnBack = $('#btn-back-chat');
    const btnTestMxcli = $('#btn-test-mxcli');
    const btnTestOpenRouter = $('#btn-test-openrouter');
    const btnCheckMcp = $('#btn-check-mcp');
    const projectBadge = $('#project-badge');
    const mcpBadge = $('#mcp-badge');
    const syncStatus = $('#sync-status');
    const welcomeMsg = chatMessages.querySelector('.welcome-msg');

    let isStreaming = false;
    let streamingFinished = false;
    let pendingRawText = '';
    let pendingMdlCommands = null;

    // --- WebView bridge (works in Studio Pro) ---
    // Only set up the mock when chrome.webview is truly absent (not in WebView2).
    // In Studio Pro, WebView2 injects chrome.webview before page scripts run.
    if (!window.chrome || !window.chrome.webview) {
        window.chrome = window.chrome || {};
        window.chrome.webview = {
            postMessage: function (msg) {
                console.log('[mock] postMessage:', msg);
            },
            addEventListener: function () {}
        };
    }

    function postMessage(message, data) {
        try {
            window.chrome.webview.postMessage({ message: message, data: data !== undefined ? data : null });
        } catch (e) {
            console.warn('[VibeCoder] postMessage failed:', e);
        }
    }

    // --- Messages from C# ---
    function onMessage(event) {
        try {
            var raw = event.data;
            // Mendix IWebView.PostMessage(string message, object? data) delivers
            // event.data as { message, data }. C# SendToWeb serializes the full
            // response object to JSON and passes it as the 'message' string.
            if (raw && typeof raw === 'object' && 'message' in raw) {
                var msgStr = raw.message;
                var payload = raw.data || {};
                if (typeof msgStr === 'string') {
                    try {
                        var parsed = JSON.parse(msgStr);
                        if (parsed && typeof parsed === 'object') {
                            handleCsharpMessage(parsed);
                            return;
                        }
                    } catch (_) {
                        // Not JSON — treat as simple message type with data
                        handleCsharpMessage(Object.assign({ type: msgStr }, payload));
                        return;
                    }
                }
            }
            // Fallback for raw string delivery
            var data = typeof raw === 'string' ? JSON.parse(raw) : raw;
            handleCsharpMessage(data);
        } catch (e) {
            console.error('[VibeCoder] Failed to parse C# message:', e);
        }
    }

    function handleCsharpMessage(data) {
        switch (data.type) {
            case 'aiStart':
                if (!isStreaming) {
                    isStreaming = true;
                    streamingFinished = false;
                    pendingRawText = '';
                    if (welcomeMsg) welcomeMsg.remove();
                    createAssistantMessage();
                    showThinking();
                }
                break;
            case 'aiChunk':
                hideThinking();
                handleAiChunk(data.content);
                break;
            case 'aiDone':
                hideThinking();
                handleAiDone(data.content, data.mdlCommandsFound, data.mdlCommands);
                break;
            case 'streamCancelled':
                hideThinking();
                handleStreamCancelled();
                break;
            case 'error':
                hideThinking();
                showError(data.message);
                finishStreaming();
                break;
            case 'contextLoaded':
                handleContextLoaded(data.context);
                break;
            case 'contextLoading':
                syncStatus.textContent = 'Loading context...';
                syncStatus.style.color = '#e8a838';
                break;
            case 'projectDetected':
                handleProjectDetected(data.path, data.name);
                break;
            case 'mdlExecuting':
                handleMdlExecuting(data.command);
                break;
            case 'mdlResult':
                handleMdlResult(data.command, data.success, data.output, data.error);
                break;
            case 'syncTriggered':
                syncStatus.textContent = 'Synced';
                syncStatus.style.color = '#4ec9b0';
                setTimeout(function () { syncStatus.textContent = ''; }, 2000);
                break;
            case 'settingsLoaded':
                populateSettings(data.settings);
                break;
            case 'settingsSaved':
                if (data.success) {
                    syncStatus.textContent = 'Saved';
                    syncStatus.style.color = '#4ec9b0';
                    setTimeout(function () { syncStatus.textContent = ''; }, 2000);
                }
                break;
            case 'mxcliTestResult':
                showTestStatus('#mxcli-test-status', data.success, data.message);
                break;
            case 'openRouterTestResult':
                showTestStatus('#openrouter-test-status', data.success, data.message);
                break;
            case 'mcpStatus':
                handleMcpStatus(data.available, data.message);
                break;
            case 'mdlConfirmationRequired':
                showMdlConfirmationDialog(data.commands);
                break;
            case 'mdlValidation':
                handleMdlValidation(data.valid, data.errors);
                break;
            default:
                console.warn('[VibeCoder] Unknown message type:', data.type, data);
        }
    }

    // --- Thinking Indicator ---
    function showThinking() {
        var existing = chatMessages.querySelector('.thinking-indicator');
        if (existing) return;
        var div = document.createElement('div');
        div.className = 'thinking-indicator';
        div.innerHTML = '<span class="thinking-dot"></span><span class="thinking-dot"></span><span class="thinking-dot"></span>';
        chatMessages.appendChild(div);
        scrollToBottom();
    }

    function hideThinking() {
        var el = chatMessages.querySelector('.thinking-indicator');
        if (el) el.remove();
    }

    // --- Error Display ---
    function showError(message) {
        var div = document.createElement('div');
        div.className = 'message error';
        div.textContent = message;
        chatMessages.appendChild(div);
        scrollToBottom();
    }

    // --- Chat ---
    function handleAiChunk(content) {
        if (!isStreaming) {
            isStreaming = true;
            streamingFinished = false;
            pendingRawText = '';
            if (welcomeMsg) welcomeMsg.remove();
            createAssistantMessage();
        }

        var last = chatMessages.querySelector('.message.assistant:last-child');
        if (last) {
            var contentEl = last.querySelector('.message-content') || last;
            pendingRawText += content;
            var openFences = (pendingRawText.match(/```/g) || []).length % 2 === 1;
            if (!openFences) {
                contentEl.innerHTML = renderMarkdown(pendingRawText);
            }
        }

        scrollToBottom();
    }

    function handleAiDone(content, mdlCount, mdlCommands) {
        var last = chatMessages.querySelector('.message.assistant:last-child');
        if (last) {
            var contentEl = last.querySelector('.message-content') || last;
            contentEl.innerHTML = renderMarkdown(pendingRawText);
            if (mdlCount > 0) {
                var mdlInfo = document.createElement('div');
                mdlInfo.className = 'mdl-inline-info';
                mdlInfo.textContent = '[' + mdlCount + ' MDL command' + (mdlCount > 1 ? 's' : '') + ' detected]';
                last.appendChild(mdlInfo);
            }
        }
        finishStreaming();
    }

    function handleStreamCancelled() {
        var last = chatMessages.querySelector('.message.assistant:last-child');
        if (last) {
            var contentEl = last.querySelector('.message-content') || last;
            contentEl.innerHTML = renderMarkdown(pendingRawText);
        }
        finishStreaming();
    }

    function finishStreaming() {
        if (streamingFinished) return;
        streamingFinished = true;
        isStreaming = false;
        btnSend.classList.remove('hidden');
        btnStop.classList.add('hidden');
        btnSend.disabled = false;
    }

    function createAssistantMessage() {
        var div = document.createElement('div');
        div.className = 'message assistant';
        chatMessages.appendChild(div);
    }

    function renderMarkdown(text) {
        var html = escapeHtml(text);

        // Code blocks
        html = html.replace(/```(\w*)\n?([\s\S]*?)```/g, function (_, lang, code) {
            return '<pre><code class="language-' + lang + '">' + code.trim() + '</code></pre>';
        });

        // Inline code
        html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

        // Bold
        html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

        // Italic
        html = html.replace(/\*([^*]+)\*/g, '<em>$1</em>');

        // Links (sanitize URLs to prevent javascript: XSS)
        html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, text, url) {
            var safeUrl = url.replace(/^(javascript|data|vbscript):/gi, '#blocked');
            return '<a href="' + safeUrl + '" target="_blank" rel="noopener" style="color:#0078d4">' + text + '</a>';
        });

        // Newlines
        html = html.replace(/\n/g, '<br>');

        return html;
    }

    function escapeHtml(str) {
        return str
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function scrollToBottom() {
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }

    function sendUserMessage() {
        var text = chatInput.value.trim();
        if (!text || isStreaming) return;

        if (welcomeMsg) welcomeMsg.remove();

        var div = document.createElement('div');
        div.className = 'message user';
        div.textContent = text;
        chatMessages.appendChild(div);
        scrollToBottom();

        chatInput.value = '';
        chatInput.style.height = 'auto';

        btnSend.classList.add('hidden');
        btnStop.classList.remove('hidden');

        postMessage('sendMessage', { message: text });
    }

    // --- MDL Inline Display ---
    function handleMdlExecuting(command) {
        var block = document.createElement('div');
        block.className = 'mdl-block mdl-executing';
        block.dataset.command = command;
        block.innerHTML =
            '<div class="mdl-block-label">MDL Command</div>' +
            '<pre><code>' + escapeHtml(command) + '</code></pre>' +
            '<div class="mdl-status executing">Executing...</div>';
        chatMessages.appendChild(block);
        scrollToBottom();
    }

    function handleMdlResult(command, success, output, error) {
        var blocks = chatMessages.querySelectorAll('.mdl-block');
        var target = null;
        blocks.forEach(function (b) { if (b.dataset.command === command) target = b; });
        if (!target) return;

        target.classList.remove('mdl-executing');
        target.classList.add(success ? 'mdl-success' : 'mdl-error');

        var statusEl = target.querySelector('.mdl-status');
        if (statusEl) {
            statusEl.classList.remove('executing');
            statusEl.classList.add(success ? 'success' : 'error');
            statusEl.textContent = success ? 'Applied successfully' : ('Failed: ' + (error || 'Unknown error'));
        }

        scrollToBottom();
    }

    // --- MDL Confirmation Dialog ---
    function showMdlConfirmationDialog(commands) {
        pendingMdlCommands = commands;
        var overlay = $('#mdl-confirm-overlay');
        var countEl = $('#mdl-confirm-count');
        var commandsEl = $('#mdl-confirm-commands');

        countEl.textContent = commands.length + ' command' + (commands.length > 1 ? 's' : '');
        commandsEl.innerHTML = '';

        commands.forEach(function (cmd, i) {
            var item = document.createElement('div');
            item.className = 'mdl-command-item';
            item.innerHTML =
                '<div class="mdl-command-label">Command ' + (i + 1) + '</div>' +
                '<pre><code>' + escapeHtml(cmd) + '</code></pre>';
            commandsEl.appendChild(item);
        });

        overlay.classList.remove('hidden');
    }

    function closeMdlDialog() {
        $('#mdl-confirm-overlay').classList.add('hidden');
        pendingMdlCommands = null;
    }

    $('#mdl-confirm-approve').addEventListener('click', function () {
        if (pendingMdlCommands) {
            postMessage('confirmMdlExecution', { approved: true, commands: pendingMdlCommands });
        }
        closeMdlDialog();
    });

    $('#mdl-confirm-reject').addEventListener('click', function () {
        postMessage('confirmMdlExecution', { approved: false, commands: [] });
        closeMdlDialog();
    });

    // --- MCP Status ---
    function handleMcpStatus(available, message) {
        if (available) {
            mcpBadge.classList.remove('hidden');
            mcpBadge.textContent = 'MCP';
            mcpBadge.title = message;
        } else {
            mcpBadge.classList.add('hidden');
        }
        showTestStatus('#mcp-test-status', available, message);
    }

    // --- MDL Validation ---
    function handleMdlValidation(valid, errors) {
        if (!valid && errors) {
            showError('MDL validation failed: ' + errors);
        }
    }

    // --- Context ---
    function handleContextLoaded(context) {
        syncStatus.textContent = 'Context loaded';
        syncStatus.style.color = '#4ec9b0';
        setTimeout(function () { syncStatus.textContent = ''; }, 3000);
    }

    // --- Project Detection ---
    function handleProjectDetected(path, name) {
        if (name) {
            projectBadge.textContent = name;
            projectBadge.classList.remove('hidden');
        } else {
            projectBadge.classList.add('hidden');
        }
    }

    // --- Settings ---
    function populateSettings(s) {
        $('#setting-apikey').value = s.openRouterApiKey || '';
        $('#setting-model').value = s.modelId || 'openrouter/free';
        $('#setting-mxcli').value = s.mxcliPath || 'mxcli';
        $('#setting-autosync').checked = s.autoSync;
        $('#setting-autocontext').checked = s.autoFetchContext;
        $('#setting-autoexecute').checked = s.autoExecuteMdl;
        $('#setting-syncdelay').value = s.syncDelayMs || 800;
        $('#setting-maxhistory').value = s.maxHistoryTokens || 120000;
        $('#setting-maxoutput').value = s.maxOutputTokens || 8192;
        $('#setting-usemcp').checked = s.useMcp;
        $('#setting-mcppart').value = s.mcpPort || 7782;
        $('#setting-mcpdial').value = s.mcpDialAddress || '127.0.0.1';
    }

    function saveSettings() {
        postMessage('saveSettings', {
            settings: {
                openRouterApiKey: $('#setting-apikey').value,
                modelId: $('#setting-model').value,
                mxcliPath: $('#setting-mxcli').value,
                autoSync: $('#setting-autosync').checked,
                autoFetchContext: $('#setting-autocontext').checked,
                autoExecuteMdl: $('#setting-autoexecute').checked,
                syncDelayMs: parseInt($('#setting-syncdelay').value) || 800,
                maxHistoryTokens: parseInt($('#setting-maxhistory').value) || 120000,
                maxOutputTokens: parseInt($('#setting-maxoutput').value) || 8192,
                useMcp: $('#setting-usemcp').checked,
                mcpPort: parseInt($('#setting-mcppart').value) || 7782,
                mcpDialAddress: $('#setting-mcpdial').value || '127.0.0.1'
            }
        });
    }

    function showTestStatus(sel, success, message, loading) {
        var el = $(sel);
        el.textContent = message;
        el.className = 'test-status ' + (loading ? 'loading' : (success ? 'pass' : 'fail'));
        setTimeout(function () { el.textContent = ''; el.className = 'test-status'; }, 5000);
    }

    // --- View Switching ---
    function showView(viewId) {
        document.querySelectorAll('.view').forEach(function (v) { v.classList.remove('active'); });
        $(viewId).classList.add('active');
    }

    // --- Events ---
    btnSend.addEventListener('click', sendUserMessage);

    chatInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendUserMessage();
        }
    });

    chatInput.addEventListener('input', function () {
        this.style.height = 'auto';
        this.style.height = Math.min(this.scrollHeight, 120) + 'px';
    });

    btnStop.addEventListener('click', function () {
        postMessage('cancelStream', null);
    });

    btnSettings.addEventListener('click', function () {
        postMessage('getSettings', null);
        showView('#settings-view');
    });

    btnBack.addEventListener('click', function () {
        showView('#chat-view');
    });

    btnSave.addEventListener('click', function () {
        saveSettings();
    });

    btnTestMxcli.addEventListener('click', function () {
        showTestStatus('#mxcli-test-status', null, 'Testing...', true);
        postMessage('testMxcli', { mxcliPath: $('#setting-mxcli').value });
    });

    btnTestOpenRouter.addEventListener('click', function () {
        showTestStatus('#openrouter-test-status', null, 'Testing...', true);
        postMessage('testOpenRouter', {
            apiKey: $('#setting-apikey').value.trim(),
            modelId: $('#setting-model').value.trim()
        });
    });

    btnCheckMcp.addEventListener('click', function () {
        showTestStatus('#mcp-test-status', null, 'Checking...', true);
        postMessage('checkMcp', null);
    });

    btnContext.addEventListener('click', function () {
        postMessage('fetchContext', null);
    });

    // --- Init ---
    // Register the message handler FIRST, then signal readiness per Mendix PostMessage API docs.
    // Messages from C# are queued until MessageListenerRegistered is sent.
    window.chrome.webview.addEventListener('message', onMessage);
    window.chrome.webview.postMessage({ message: 'MessageListenerRegistered' });

    // Now safe to send initial messages — C# queue will deliver them after JS is ready.
    postMessage('detectProject', null);
    postMessage('checkMcp', null);
})();
