namespace Benow.Conversation.Settings;

/// <summary>
/// The settings page markup. Deliberately a single self-contained document: no build step, no
/// external assets, no CDN — it is served from a loopback listener inside the desktop app.
/// </summary>
internal static class SettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect x='2' y='2' width='28' height='28' rx='7' fill='%23e6a95a'/%3E%3Ccircle cx='10' cy='16' r='2.5' fill='%2316181d'/%3E%3Ccircle cx='16' cy='16' r='2.5' fill='%2316181d'/%3E%3Ccircle cx='22' cy='16' r='2.5' fill='%2316181d'/%3E%3C/svg%3E">
<title>Conversation — Settings</title>
<style>
  :root { color-scheme: dark; --bg:#16181d; --card:#1e2128; --fg:#e6e8ec; --dim:#9aa1ad; --acc:#5aa9e6; --ok:#4fbf7a; --err:#e06c75; }
  * { box-sizing: border-box; }
  body { margin:0; padding:28px; background:var(--bg); color:var(--fg);
         font:14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
  h1 { font-size:19px; margin:0 0 4px; }
  .sub { color:var(--dim); margin-bottom:22px; }
  .grid { display:grid; gap:16px; grid-template-columns:repeat(auto-fit, minmax(320px, 1fr)); max-width:1100px; }
  section { background:var(--card); border:1px solid #2a2e37; border-radius:10px; padding:16px 18px; }
  section h2 { font-size:14px; margin:0 0 12px; letter-spacing:.02em; text-transform:uppercase; color:var(--dim); }
  label { display:block; margin:10px 0 3px; color:var(--dim); font-size:12px; }
  input, select, textarea { width:100%; padding:7px 9px; border-radius:6px; border:1px solid #333844;
                            background:#12141a; color:var(--fg); font:inherit; }
  textarea { min-height:72px; resize:vertical; }
  .row { display:flex; gap:10px; } .row > * { flex:1; }
  button { margin-top:14px; padding:8px 14px; border-radius:6px; border:0; background:var(--acc);
           color:#0b1016; font-weight:600; cursor:pointer; }
  button.ghost { background:transparent; color:var(--acc); border:1px solid #35506b; font-weight:500; }
  button:disabled { opacity:.5; cursor:default; }
  .status { margin-top:10px; font-size:12px; min-height:16px; }
  .ok { color:var(--ok); } .err { color:var(--err); }
  .pill { display:inline-block; padding:1px 7px; border-radius:99px; background:#252a34; color:var(--dim); font-size:11px; margin-left:6px; }
  ul { list-style:none; margin:0; padding:0; } li { display:flex; justify-content:space-between; gap:8px;
       padding:6px 0; border-bottom:1px solid #262a33; } li:last-child { border-bottom:0; }
  .hint { color:var(--dim); font-size:12px; margin-top:6px; }
  .active { color:var(--ok); font-size:12px; }
</style>
</head>
<body>
<h1>Conversation</h1>
<div class="sub">Local settings — served from the app on <code id="addr"></code>. Keys are stored in <code>~/.config/conversation/config.json</code> (0600) and never displayed back.</div>

<div class="grid">
  <section>
    <h2>Providers</h2>
    <label>Groq API key (STT)</label><input id="groq" type="password" placeholder="stored — type to replace">
    <label>OpenRouter API key (chat / TTS)</label><input id="openrouter" type="password" placeholder="stored — type to replace">
    <label>Replicate API token (voice clone TTS)</label><input id="replicate" type="password" placeholder="stored — type to replace">
    <label>OpenAI-compatible base URL key (Ollama)</label><input id="oai" type="password" placeholder="optional — leave blank for Ollama">
  </section>

  <section>
    <h2>Speech to text <span class="pill">global</span></h2>
    <label>Provider</label>
    <select id="sttProvider"><option>groq</option><option>openai-compatible</option></select>
    <label>Model</label><input id="sttModel">
    <label>Language</label><input id="sttLanguage" placeholder="en">
  </section>

  <section>
    <h2>Chat</h2>
    <label>Provider</label>
    <select id="aiProvider"><option>openrouter</option><option>openai-compatible</option><option>groq</option></select>
    <label>Speaker model</label><input id="llmModel">
    <label>Extractor model <span class="pill">global</span></label><input id="extractorModel">
    <label>Default system prompt</label><textarea id="llmSystemPrompt"></textarea>
  </section>

  <section>
    <h2>Text to speech</h2>
    <label>Provider</label>
    <select id="ttsProvider"><option>replicate</option><option>openai</option><option>kokoro</option></select>
    <label>Model</label><input id="ttsModel">
    <label>Voice</label><input id="ttsVoice">
    <button id="speakTest" class="ghost">Speak test phrase</button>
    <div class="hint" id="ttsStatus"></div>
  </section>

  <section>
    <h2>Audio</h2>
    <label>Input device</label><select id="inputDevice"></select>
    <label>Output device</label><input id="outputDevice" placeholder="default">
    <label>Playback volume (%)</label><input id="playbackVolume" type="number" min="0" max="100">
  </section>

  <section>
    <h2>Personas</h2>
    <ul id="personaList"></ul>
    <label>Name</label><input id="personaName">
    <label>System prompt</label><textarea id="personaPrompt"></textarea>
    <div class="row">
      <div><label>Speaker model</label><input id="personaModel"></div>
      <div><label>TTS voice</label><input id="personaVoice"></div>
    </div>
    <button id="personaSave">Save persona</button>
    <div class="hint" id="personaStatus"></div>
  </section>

  <section>
    <h2>Voices</h2>
    <ul id="voiceList"></ul>
    <div class="hint" id="voiceDir"></div>
  </section>
</div>

<button id="save" style="margin-top:22px">Save settings</button>
<div class="status" id="status"></div>

<script>
const $ = id => document.getElementById(id);
const status = (el, msg, ok) => { el.textContent = msg; el.className = 'status ' + (ok ? 'ok' : 'err'); };
$('addr').textContent = location.host;

async function api(path, opts) {
  const r = await fetch(path, opts);
  const text = await r.text();
  let body; try { body = JSON.parse(text); } catch { body = text; }
  if (!r.ok) throw new Error((body && body.error) || r.statusText);
  return body;
}

async function load() {
  const c = await api('/api/config');
  for (const [k, v] of Object.entries({ groq:'groq', openrouter:'openRouter', replicate:'replicate', oai:'openAiCompat' })) {
    $(k).placeholder = c.providerKeys[v] ? 'stored — type to replace' : 'not set';
  }
  $('sttProvider').value = c.stt.provider || 'groq';
  $('sttModel').value = c.stt.model || '';
  $('sttLanguage').value = c.stt.language || '';
  $('aiProvider').value = c.chat.provider || 'openrouter';
  $('llmModel').value = c.chat.model || '';
  $('extractorModel').value = c.chat.extractor || '';
  $('llmSystemPrompt').value = c.chat.systemPrompt || '';
  $('ttsProvider').value = c.tts.provider || 'replicate';
  $('ttsModel').value = c.tts.model || '';
  $('ttsVoice').value = c.tts.voice || '';
  $('outputDevice').value = c.audio.outputDevice || '';
  $('playbackVolume').value = c.audio.volume ?? 100;

  const d = await api('/api/devices');
  const sel = $('inputDevice');
  sel.innerHTML = '<option value="">system default</option>' +
    d.inputs.map(i => `<option${i === d.current ? ' selected' : ''}>${i}</option>`).join('');

  const v = await api('/api/voices');
  $('voiceDir').textContent = v.directory;
  $('voiceList').innerHTML = v.voices.length
    ? v.voices.map(x => `<li><span>${x.name}</span></li>`).join('')
    : '<li><span class="err">no voices yet — import or record in the app</span></li>';

  const p = await api('/api/personas');
  $('personaList').innerHTML = p.personas.map(x =>
    `<li><span>${x.name}${x.name === p.active ? ' <span class="active">● active</span>' : ''}</span>
      <span><button class="ghost" data-use="${x.name}">use</button></span></li>`).join('');
  for (const b of document.querySelectorAll('[data-use]')) {
    b.onclick = async () => { await api('/api/personas/active', json({ name: b.dataset.use })); load(); };
  }
}

const json = body => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

$('save').onclick = async () => {
  try {
    await api('/api/config', json({
      groqApiKey: $('groq').value, openRouterApiKey: $('openrouter').value,
      replicateApiKey: $('replicate').value, openAiCompatApiKey: $('oai').value,
      sttProvider: $('sttProvider').value, sttModel: $('sttModel').value, sttLanguage: $('sttLanguage').value,
      aiProvider: $('aiProvider').value, llmModel: $('llmModel').value,
      extractorModel: $('extractorModel').value, llmSystemPrompt: $('llmSystemPrompt').value,
      ttsProvider: $('ttsProvider').value, ttsModel: $('ttsModel').value, ttsVoice: $('ttsVoice').value,
      inputDevice: $('inputDevice').value, outputDevice: $('outputDevice').value,
      playbackVolume: parseInt($('playbackVolume').value || '100', 10)
    }));
    status($('status'), 'Saved. Restart the app to apply engine changes.', true);
    load();
  } catch (e) { status($('status'), e.message, false); }
};

$('personaSave').onclick = async () => {
  try {
    await api('/api/personas', json({ name: $('personaName').value, systemPrompt: $('personaPrompt').value,
      speakerModel: $('personaModel').value, ttsVoice: $('personaVoice').value }));
    status($('personaStatus'), 'Saved.', true); load();
  } catch (e) { status($('personaStatus'), e.message, false); }
};

$('speakTest').onclick = async () => {
  $('speakTest').disabled = true; status($('ttsStatus'), 'synthesizing…', true);
  try { const r = await api('/api/voice-test', json({})); status($('ttsStatus'), r.result, true); }
  catch (e) { status($('ttsStatus'), e.message, false); }
  finally { $('speakTest').disabled = false; }
};

load().catch(e => status($('status'), e.message, false));
</script>
</body>
</html>
""";
}
