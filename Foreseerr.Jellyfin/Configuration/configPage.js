// Controller of configPage.html, loaded by Jellyfin Web through
// data-controller="__plugin/ForeseerrConfigPage.js".
const pluginId = 'a7c3e2f1-9b4d-4e8a-8c1f-2b6d9e0f4a11';
// PluginConfiguration.Access, in enum order.
const accessModes = ['Everyone', 'Administrators'];
const publicUrlHelp =
  "Links in Foreseerr notifications point here. Leave it empty to use Jellyfin's published server URI (Networking settings) or the Application URL set in Foreseerr.";

function getJson(path) {
  return ApiClient.ajax({
    type: 'GET',
    url: ApiClient.getUrl(path),
    dataType: 'json',
  });
}

function time(value) {
  return new Date(value).toLocaleTimeString();
}

function duration(since) {
  const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(since)) / 60000));
  if (minutes < 1) return 'less than a minute';
  if (minutes < 60) return minutes + ' min';
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return hours + ' h ' + (minutes % 60) + ' min';
  return Math.floor(hours / 24) + ' days';
}

/** The headline and its colour for a /ForeseerrPlugin/Status answer. */
function describe(status) {
  switch (status.state) {
    case 'running':
      return status.unresponsiveSince
        ? {
            tone: 'busy',
            text:
              'Foreseerr is running but has not answered since ' +
              time(status.unresponsiveSince) +
              '.',
          }
        : { tone: 'ok', text: 'Foreseerr is running.' };
    case 'starting':
      return { tone: 'busy', text: 'Foreseerr is starting…' };
    case 'stopping':
      return { tone: 'busy', text: 'Foreseerr is stopping…' };
    case 'waiting':
      return {
        tone: 'error',
        text:
          'Foreseerr stopped.' +
          (status.nextAttempt
            ? ' Next start at ' + time(status.nextAttempt) + '.'
            : ''),
      };
    case 'disabled':
      return { tone: 'off', text: 'Foreseerr is turned off.' };
    default:
      return { tone: 'off', text: 'Foreseerr is not running.' };
  }
}

/** The JSON error a refused request explains itself with, if any. */
function reason(response, fallback) {
  return response
    .json()
    .catch(function () {
      return {};
    })
    .then(function (body) {
      throw new Error(body.detail || body.message || fallback);
    });
}

export default function (view) {
  let timer = null;
  // Requests still in flight when the page is left must not start polling again.
  let visible = false;

  function $(selector) {
    return view.querySelector(selector);
  }

  function schedule(delay) {
    clearTimeout(timer);
    if (visible) timer = setTimeout(loadStatus, delay);
  }

  function later(action, delay) {
    setTimeout(function () {
      if (visible) action();
    }, delay);
  }

  function renderStatus(status) {
    const headline = describe(status);
    $('#StatusDot').dataset.tone = headline.tone;
    $('#StatusText').textContent = headline.text;

    const details = [];
    if (status.foreseerrVersion) details.push('Foreseerr ' + status.foreseerrVersion);
    if (status.version) details.push('plugin ' + status.version);
    if (status.pid) details.push('process ' + status.pid);
    if (status.readySince) details.push('up ' + duration(status.readySince));
    if (status.restarts)
      details.push(
        'restarted ' + status.restarts + (status.restarts === 1 ? ' time' : ' times')
      );
    $('#StatusDetails').textContent = details.join(' · ');

    const error = $('#StatusError');
    if (status.lastError && (status.state === 'waiting' || status.state === 'starting')) {
      error.textContent = 'Last error: ' + status.lastError;
      error.classList.remove('hide');
    } else {
      error.classList.add('hide');
    }

    const disabled = status.state === 'disabled';
    $('#OpenForeseerr').disabled = disabled;
    $('#RestartForeseerr').disabled = disabled;
    $('#DataDirectory').textContent = status.dataDirectory || '';
    $('#HeaderButtonDescription').textContent = status.headerButton
      ? 'File Transformation is installed, so Jellyfin Web can show the button. Turning this off hides it at the next page load.'
      : 'Needs the File Transformation plugin, which is not installed. Without it, share the direct link below.';
    $('#PublicServerUrlDescription').textContent =
      publicUrlHelp +
      (status.publicUrl
        ? ' In use: ' + status.publicUrl
        : ' None is set, so Foreseerr uses its own Application URL, if any.');

    // Poll quickly while something is about to change.
    schedule(status.state === 'running' && !status.unresponsiveSince ? 15000 : 2000);
  }

  function loadStatus() {
    return getJson('ForeseerrPlugin/Status')
      .then(renderStatus)
      .catch(function () {
        $('#StatusDot').dataset.tone = 'error';
        $('#StatusText').textContent = 'Could not read the Foreseerr status.';
        schedule(15000);
      });
  }

  function loadConfiguration() {
    Dashboard.showLoadingMsg();
    return ApiClient.getPluginConfiguration(pluginId)
      .then(function (config) {
        $('#Enabled').checked = config.Enabled !== false;
        const access =
          typeof config.Access === 'number'
            ? config.Access
            : Math.max(0, accessModes.indexOf(config.Access));
        $('#Access').value = String(access);
        $('#HeaderButton').checked = config.HeaderButton !== false;
        $('#PublicServerUrl').value = config.PublicServerUrl || '';
      })
      .finally(function () {
        Dashboard.hideLoadingMsg();
      });
  }

  function loadLog() {
    return getJson('ForeseerrPlugin/Logs')
      .then(function (result) {
        const log = $('#ForeseerrLog');
        const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 8;
        log.replaceChildren();
        result.lines.forEach(function (line) {
          const row = document.createElement('div');
          row.className = line.level;
          row.textContent = time(line.time) + '  ' + line.message;
          log.appendChild(row);
        });
        if (!result.lines.length) log.textContent = 'Nothing yet.';
        if (atBottom) log.scrollTop = log.scrollHeight;
      })
      .catch(function () {
        $('#ForeseerrLog').textContent = 'Could not read the output.';
      });
  }

  function validPublicUrl(value) {
    if (!value) return true;
    try {
      const url = new URL(value);
      return (
        (url.protocol === 'http:' || url.protocol === 'https:') &&
        !url.username &&
        !url.password &&
        !url.search &&
        !url.hash
      );
    } catch {
      return false;
    }
  }

  $('#ForeseerrConfigForm').addEventListener('submit', function (event) {
    event.preventDefault();
    const publicUrl = $('#PublicServerUrl').value.trim();
    if (!validPublicUrl(publicUrl)) {
      Dashboard.alert(
        'Enter an http or https address, for example https://jellyfin.example.com.'
      );
      return false;
    }
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId)
      .then(function (config) {
        config.Enabled = $('#Enabled').checked;
        // A number parses whether or not Jellyfin reads enums as strings.
        config.Access = Number($('#Access').value);
        config.HeaderButton = $('#HeaderButton').checked;
        config.PublicServerUrl = publicUrl;
        return ApiClient.updatePluginConfiguration(pluginId, config);
      })
      .then(function (result) {
        Dashboard.processPluginConfigurationUpdateResult(result);
        // A setting Foreseerr reads restarts it; show that right away.
        later(function () {
          loadStatus();
          loadLog();
        }, 1000);
      })
      .catch(function () {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('The settings could not be saved. Check the values and try again.');
      });
    return false;
  });

  $('#OpenForeseerr').addEventListener('click', function () {
    fetch(ApiClient.getUrl('Foreseerr/sso'), {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"',
      },
    })
      .then(function (response) {
        if (response.ok) return response.json();
        return reason(
          response,
          'Foreseerr sign-in is unavailable. Check the status above and retry.'
        );
      })
      .then(function (body) {
        window.location.href = body.url;
      })
      .catch(function (error) {
        Dashboard.alert(error.message);
      });
  });

  $('#RestartForeseerr').addEventListener('click', function () {
    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ForeseerrPlugin/Restart') })
      .then(function () {
        $('#StatusDot').dataset.tone = 'busy';
        $('#StatusText').textContent = 'Restarting Foreseerr…';
        schedule(1500);
        later(loadLog, 1500);
      })
      .catch(function () {
        Dashboard.alert('Foreseerr could not be restarted. Is it turned on?');
      });
  });

  $('#RefreshLog').addEventListener('click', loadLog);

  view.addEventListener('viewshow', function () {
    visible = true;
    const link = $('#ForeseerrLink');
    link.href = ApiClient.getUrl('Foreseerr/');
    link.textContent = link.href;
    // Opening one of these signs in with this browser's Jellyfin login.
    view.querySelectorAll('[data-foreseerr-path]').forEach(function (anchor) {
      anchor.href = ApiClient.getUrl('Foreseerr/' + anchor.dataset.foreseerrPath);
    });
    $('#PublicServerUrlDescription').textContent = publicUrlHelp;
    loadConfiguration();
    loadStatus();
    loadLog();
  });

  view.addEventListener('viewhide', function () {
    visible = false;
    clearTimeout(timer);
  });
}
