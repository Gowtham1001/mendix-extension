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
    let pendingMdlCommands = null;

    // --- WebView bridge (works in Studio Pro) ---
    function postToCsharp(data) {
        try {
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage(JSON.stringify(data));
            }
        } catch (e) { /* extension not active */ }
    }

    window.chrome = window.chrome || {};
    window.chrome.webview = window.chrome.webview || {
        postMessage: function (msg) {
            console.log('[mock] postMessage:', msg);
        }
    };

    // --- Messages from C# ---
    function onMessage(event) {
        try {
            const data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
            handleCsharpMessage(data);
        } catch (e) {
            console.error('Failed to parse C# message:', e);
        }
    }

    function handleCsharpMessage(data) {
        switch (data.type) {
            case 'aiChunk':
                handleAiChunk(data.content);
                break;
            case 'aiDone':
                handleAiDone(data.content, data.mdlCommandsFound, data.mdlCommands);
                break;
            case 'streamCancelled':
                finishStreaming();
                break;
            case 'error':
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
                setTimeout(() => { syncStatus.textContent = ''; }, 2000);
                break;
            case 'settingsLoaded':
                populateSettings(data.settings);
                break;
            case 'settingsSaved':
                if (data.success) {
                    syncStatus.textContent = 'Saved';
                    syncStatus.style.color = '#4ec9b0';
                    setTimeout(() => { syncStatus.textContent = ''; }, 2000);
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
        }
    }

    // --- Error Display ---
    function showError(message) {
        const div = document.createElement('div');
        div.className = 'message error';
        div.textContent = message;
        chatMessages.appendChild(div);
        scrollToBottom();
    }

    // --- Chat ---
    function handleAiChunk(content) {
        if (!isStreaming) {
            isStreaming = true;
            if (welcomeMsg) welcomeMsg.remove();
            createAssistantMessage();
        }

        const last = chatMessages.querySelector('.message.assistant:last-child');
        if (last) {
            const contentEl = last.querySelector('.message-content') || last;
            appendMarkdown(contentEl, content);
        }

        scrollToBottom();
    }

    function handleAiDone(content, mdlCount, mdlCommands) {
        const last = chatMessages.querySelector('.message.assistant:last-child');
        if (last && mdlCount > 0) {
            const mdlInfo = document.createElement('div');
            mdlInfo.className = 'mdl-inline-info';
            mdlInfo.textContent = `[${mdlCount} MDL command${mdlCount > 1 ? 's' : ''} detected]`;
            last.appendChild(mdlInfo);
        }
        finishStreaming();
    }

    function finishStreaming() {
        isStreaming = false;
        btnSend.classList.remove('hidden');
        btnStop.classList.add('hidden');
        btnSend.disabled = false;
    }

    function createAssistantMessage() {
        const div = document.createElement('div');
        div.className = 'message assistant';
        chatMessages.appendChild(div);
    }

    function appendMarkdown(el, text) {
        el.innerHTML = el.innerHTML + renderMarkdown(text);
    }

    function renderMarkdown(text) {
        let html = escapeHtml(text);

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

        // Links
        html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank" style="color:#0078d4">$1</a>');

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
        const text = chatInput.value.trim();
        if (!text || isStreaming) return;

        if (welcomeMsg) welcomeMsg.remove();

        const div = document.createElement('div');
        div.className = 'message user';
        div.textContent = text;
        chatMessages.appendChild(div);
        scrollToBottom();

        chatInput.value = '';
        chatInput.style.height = 'auto';

        btnSend.classList.add('hidden');
        btnStop.classList.remove('hidden');

        postToCsharp({ type: 'sendMessage', message: text });
    }

    // --- MDL Inline Display ---
    function handleMdlExecuting(command) {
        const block = document.createElement('div');
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
        const blocks = chatMessages.querySelectorAll('.mdl-block');
        let target = null;
        blocks.forEach(b => { if (b.dataset.command === command) target = b; });
        if (!target) return;

        target.classList.remove('mdl-executing');
        target.classList.add(success ? 'mdl-success' : 'mdl-error');

        const statusEl = target.querySelector('.mdl-status');
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
        const overlay = $('#mdl-confirm-overlay');
        const countEl = $('#mdl-confirm-count');
        const commandsEl = $('#mdl-confirm-commands');

        countEl.textContent = commands.length + ' command' + (commands.length > 1 ? 's' : '');
        commandsEl.innerHTML = '';

        commands.forEach(function (cmd, i) {
            const item = document.createElement('div');
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
            postToCsharp({
                type: 'confirmMdlExecution',
                approved: true,
                commands: pendingMdlCommands
            });
        }
        closeMdlDialog();
    });

    $('#mdl-confirm-reject').addEventListener('click', function () {
        postToCsharp({ type: 'confirmMdlExecution', approved: false, commands: [] });
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
        setTimeout(() => { syncStatus.textContent = ''; }, 3000);
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
        $('#setting-model').value = s.modelId || 'anthropic/claude-sonnet-4-20250514';
        $('#setting-mxcli').value = s.mxcliPath || 'mxcli';
        $('#setting-autosync').checked = s.autoSync;
        $('#setting-autocontext').checked = s.autoFetchContext;
        $('#setting-autoexecute').checked = s.autoExecuteMdl;
        $('#setting-syncdelay').value = s.syncDelayMs || 800;
        $('#setting-maxhistory').value = s.maxHistoryTokens || 120000;
        $('#setting-usemcp').checked = s.useMcp;
        $('#setting-mcppart').value = s.mcpPort || 7782;
        $('#setting-mcpdial').value = s.mcpDialAddress || '127.0.0.1';
    }

    function saveSettings() {
        postToCsharp({
            type: 'saveSettings',
            settings: {
                openRouterApiKey: $('#setting-apikey').value,
                modelId: $('#setting-model').value,
                mxcliPath: $('#setting-mxcli').value,
                autoSync: $('#setting-autosync').checked,
                autoFetchContext: $('#setting-autocontext').checked,
                autoExecuteMdl: $('#setting-autoexecute').checked,
                syncDelayMs: parseInt($('#setting-syncdelay').value) || 800,
                maxHistoryTokens: parseInt($('#setting-maxhistory').value) || 120000,
                useMcp: $('#setting-usemcp').checked,
                mcpPort: parseInt($('#setting-mcppart').value) || 7782,
                mcpDialAddress: $('#setting-mcpdial').value || '127.0.0.1'
            }
        });
    }

    function showTestStatus(sel, success, message, loading) {
        const el = $(sel);
        el.textContent = message;
        el.className = 'test-status ' + (loading ? 'loading' : (success ? 'pass' : 'fail'));
        setTimeout(() => { el.textContent = ''; }, 5000);
    }

    // --- View Switching ---
    function showView(viewId) {
        document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
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
        postToCsharp({ type: 'cancelStream' });
        finishStreaming();
    });

    btnSettings.addEventListener('click', function () {
        postToCsharp({ type: 'getSettings' });
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
        postToCsharp({ type: 'testMxcli' });
    });

    btnTestOpenRouter.addEventListener('click', function () {
        showTestStatus('#openrouter-test-status', null, 'Testing...', true);
        postToCsharp({ type: 'testOpenRouter' });
    });

    btnCheckMcp.addEventListener('click', function () {
        showTestStatus('#mcp-test-status', null, 'Checking...', true);
        postToCsharp({ type: 'checkMcp' });
    });

    btnContext.addEventListener('click', function () {
        postToCsharp({ type: 'fetchContext' });
    });

    // --- Init ---
    window.addEventListener('message', onMessage);
    postToCsharp({ type: 'detectProject' });
    postToCsharp({ type: 'checkMcp' });
})();
