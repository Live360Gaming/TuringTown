import Phaser from 'phaser';

export const NetworkEvents = {
    REQUEST: 'request',   // (req)
    RESPONSE: 'response', // (res)  res = {method, path, status, data}
    ERROR: 'error',       // (err)  NetworkError
    POLL: 'poll',         // (name, data)
};

export class NetworkError extends Error {
    constructor(message, {status = 0, data = null, method, path, cause} = {}) {
        super(message, {cause});
        this.name = 'NetworkError';
        this.status = status;
        this.data = data;
        this.method = method;
        this.path = path;
    }
}

/**
 * HTTP network manager built on Phaser's event emitter.
 *
 * Every call both returns a promise AND emits events, so scenes can either
 * `await net.post(...)` or subscribe with `net.on('response', ...)`.
 * Since HTTP has no server push, `startPolling` emits `poll` events (plus
 * `poll:<name>`) on an interval.
 */
export class NetworkManager extends Phaser.Events.EventEmitter {
    constructor({baseUrl = '', timeout = 10000, headers = {}} = {}) {
        super();
        this.baseUrl = baseUrl.replace(/\/+$/, '');
        this.timeout = timeout;
        this.headers = {'Content-Type': 'application/json', ...headers};
        this.polls = new Map();
    }

    setHeader(name, value) {
        if (value == null) delete this.headers[name];
        else this.headers[name] = value;
    }

    async request(method, path, {body, headers, timeout = this.timeout, signal} = {}) {
        method = method.toUpperCase();
        const url = this.baseUrl + path;
        const controller = new AbortController();
        const onAbort = () => controller.abort();
        signal?.addEventListener('abort', onAbort);
        const timer = setTimeout(() => controller.abort(), timeout);

        this.emit(NetworkEvents.REQUEST, {method, path, body});

        try {
            let res;
            try {
                res = await fetch(url, {
                    method,
                    headers: {...this.headers, ...headers},
                    body: body === undefined ? undefined : JSON.stringify(body),
                    signal: controller.signal,
                });
            } catch (cause) {
                const timedOut = controller.signal.aborted && !signal?.aborted;
                throw new NetworkError(timedOut ? 'Request timed out' : 'Network request failed', {
                    method, path, cause,
                });
            }

            const text = await res.text();
            let data = null;
            if (text) {
                try { data = JSON.parse(text); } catch { data = text; }
            }

            if (!res.ok) {
                throw new NetworkError(data?.error ?? data?.message ?? `HTTP ${res.status}`, {
                    status: res.status, data, method, path,
                });
            }

            this.emit(NetworkEvents.RESPONSE, {method, path, status: res.status, data});
            return data;
        } catch (err) {
            this.emit(NetworkEvents.ERROR, err);
            throw err;
        } finally {
            clearTimeout(timer);
            signal?.removeEventListener('abort', onAbort);
        }
    }

    get(path, opts) { return this.request('GET', path, opts); }
    post(path, body, opts) { return this.request('POST', path, {...opts, body}); }
    put(path, body, opts) { return this.request('PUT', path, {...opts, body}); }
    delete(path, opts) { return this.request('DELETE', path, opts); }

    /**
     * Repeatedly GET `path` and emit `poll` / `poll:<name>` with the result.
     * Waits for each request to finish before scheduling the next, so slow
     * responses never pile up. Errors are emitted via `error` and polling continues.
     */
    startPolling(name, path, interval = 1000) {
        this.stopPolling(name);
        const state = {stopped: false, timer: null, controller: new AbortController()};
        this.polls.set(name, state);

        const tick = async () => {
            try {
                const data = await this.get(path, {signal: state.controller.signal});
                if (state.stopped) return;
                this.emit(NetworkEvents.POLL, name, data);
                this.emit(`${NetworkEvents.POLL}:${name}`, data);
            } catch { /* already emitted as 'error' */ }
            if (!state.stopped) state.timer = setTimeout(tick, interval);
        };
        tick();
    }

    stopPolling(name) {
        const state = this.polls.get(name);
        if (!state) return;
        state.stopped = true;
        clearTimeout(state.timer);
        state.controller.abort();
        this.polls.delete(name);
    }

    destroy() {
        for (const name of [...this.polls.keys()]) this.stopPolling(name);
        this.removeAllListeners();
    }
}
