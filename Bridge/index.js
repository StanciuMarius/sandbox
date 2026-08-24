#!/usr/bin/env node

/**
 * MCP bridge between a coding agent and a live s&box Sandbox session.
 *
 * The game cannot accept connections - s&box gives game code a WebSocket client
 * and no listener - so the game dials out to us and we hold the socket. We are
 * launched by the agent over stdio, so the player never runs or manages a process.
 *
 * This is deliberately a translator and nothing else. The verb table arrives from
 * the game in its `hello` and becomes our tool list, so adding a verb in
 * Code/AgenticBridge/AgentVerbs.cs is all it takes for an agent to see it - this
 * package does not need republishing and has no idea what any verb means.
 */

import { WebSocketServer } from "ws";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
	ListToolsRequestSchema,
	CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

/**
 * s&box only permits localhost on these ports, so the game scans them in this
 * order. We bind the first one that's free and it finds us.
 */
const CANDIDATE_PORTS = [8080, 8443, 80, 443];

/** How long to wait for the game to answer a verb before giving up. */
const CALL_TIMEOUT_MS = 15000;

/** stdout is the MCP channel - anything we write there corrupts the protocol. */
const log = (...args) => console.error("[bridge]", ...args);

class GameLink {
	constructor() {
		this.socket = null;
		this.verbs = [];
		this.info = {};
		this.pending = new Map();
		this.nextId = 1;
		this.onVerbsChanged = () => {};
	}

	get connected() {
		return this.socket !== null && this.socket.readyState === 1;
	}

	attach(socket) {
		// one session at a time; a new game replaces the old link
		if (this.socket) {
			log("replacing existing game connection");
			try {
				this.socket.close();
			} catch {}
		}

		this.socket = socket;

		socket.on("message", (data) => this.onMessage(data));

		socket.on("close", () => {
			if (this.socket === socket) {
				this.socket = null;
				this.verbs = [];
				this.info = {};
				this.failAllPending("game disconnected");
				this.onVerbsChanged();
				log("game disconnected");
			}
		});

		socket.on("error", (err) => log("socket error:", err.message));
	}

	onMessage(data) {
		let msg;

		try {
			msg = JSON.parse(data.toString());
		} catch {
			log("ignoring non-JSON frame");
			return;
		}

		// the game announces its verb table on connect
		if (msg.type === "hello") {
			this.verbs = Array.isArray(msg.verbs) ? msg.verbs : [];
			this.info = { game: msg.game, isHost: msg.isHost };

			log(
				`game connected: ${msg.game ?? "unknown"}` +
					`${msg.isHost ? " (host)" : ""}, ${this.verbs.length} verbs`,
			);

			this.onVerbsChanged();
			return;
		}

		// otherwise it's a reply to one of our calls
		const entry = this.pending.get(msg.id);
		if (!entry) return;

		this.pending.delete(msg.id);
		clearTimeout(entry.timer);
		entry.resolve(msg);
	}

	call(verb, args) {
		if (!this.connected) {
			return Promise.reject(
				new Error(
					"No Sandbox session connected. In the game console run 'sb.bridge true' " +
						"then 'bridge_connect', and make sure you are in a session.",
				),
			);
		}

		const id = String(this.nextId++);

		return new Promise((resolve, reject) => {
			const timer = setTimeout(() => {
				this.pending.delete(id);
				reject(new Error(`Timed out after ${CALL_TIMEOUT_MS}ms waiting for '${verb}'`));
			}, CALL_TIMEOUT_MS);

			this.pending.set(id, { resolve, reject, timer });

			try {
				this.socket.send(JSON.stringify({ id, verb, args: args ?? {} }));
			} catch (err) {
				this.pending.delete(id);
				clearTimeout(timer);
				reject(err);
			}
		});
	}

	failAllPending(reason) {
		for (const [, entry] of this.pending) {
			clearTimeout(entry.timer);
			entry.reject(new Error(reason));
		}
		this.pending.clear();
	}
}

/**
 * The game sends each argument as name -> human description. MCP wants JSON
 * Schema, and we have no type information, so everything is a documented string.
 * The description carries the real contract.
 */
function toolFromVerb(verb) {
	const properties = {};

	for (const [name, description] of Object.entries(verb.args ?? {})) {
		properties[name] = { type: "string", description };
	}

	return {
		name: verb.name,
		description: verb.description,
		inputSchema: {
			type: "object",
			properties,
			// the game validates and reports what it actually needs
			required: [],
		},
	};
}

async function listen(link) {
	for (const port of CANDIDATE_PORTS) {
		try {
			const wss = await new Promise((resolve, reject) => {
				// bind loopback only - this must never be reachable off-machine
				const server = new WebSocketServer({ port, host: "127.0.0.1" });
				server.once("listening", () => resolve(server));
				server.once("error", reject);
			});

			wss.on("connection", (socket) => link.attach(socket));

			log(`listening on ws://localhost:${port}/`);
			return port;
		} catch (err) {
			if (err.code === "EADDRINUSE" || err.code === "EACCES") {
				log(`port ${port} unavailable (${err.code}), trying next`);
				continue;
			}
			throw err;
		}
	}

	throw new Error(
		`Could not bind any of ${CANDIDATE_PORTS.join(", ")}. s&box only allows ` +
			`localhost on those ports, so one must be free.`,
	);
}

async function main() {
	const link = new GameLink();

	const server = new Server(
		{ name: "sandbox-bridge", version: "0.1.0" },
		{ capabilities: { tools: { listChanged: true } } },
	);

	// the tool list is whatever the connected game says it is
	link.onVerbsChanged = () => {
		server
			.sendToolListChanged()
			.catch((err) => log("failed to notify tool list change:", err.message));
	};

	server.setRequestHandler(ListToolsRequestSchema, async () => {
		if (!link.connected) {
			// a tool list that explains its own emptiness beats a bare []
			return {
				tools: [
					{
						name: "sandbox_status",
						description:
							"Report whether a Sandbox session is connected to this bridge. " +
							"No game is connected yet - start Sandbox, join a session, and in the " +
							"console run 'sb.bridge true' then 'bridge_connect'. The real tools " +
							"appear here once the game connects.",
						inputSchema: { type: "object", properties: {}, required: [] },
					},
				],
			};
		}

		return { tools: link.verbs.map(toolFromVerb) };
	});

	server.setRequestHandler(CallToolRequestSchema, async (request) => {
		const { name, arguments: args } = request.params;

		if (name === "sandbox_status") {
			return {
				content: [
					{
						type: "text",
						text: link.connected
							? `Connected to ${link.info.game}. ${link.verbs.length} verbs available.`
							: "No Sandbox session connected.",
					},
				],
			};
		}

		try {
			const reply = await link.call(name, args);

			if (!reply.ok) {
				// a verb that failed is a result the agent should read and adapt to,
				// not a protocol error
				return {
					isError: true,
					content: [{ type: "text", text: String(reply.error ?? "unknown error") }],
				};
			}

			return {
				content: [
					{ type: "text", text: JSON.stringify(reply.result ?? {}, null, 2) },
				],
			};
		} catch (err) {
			return { isError: true, content: [{ type: "text", text: err.message }] };
		}
	});

	await listen(link);

	await server.connect(new StdioServerTransport());
	log("MCP server ready on stdio");
}

main().catch((err) => {
	log("fatal:", err.message);
	process.exit(1);
});
