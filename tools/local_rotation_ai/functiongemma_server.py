#!/usr/bin/env python3
import argparse
import json
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


FUNCTION_CALL_PATTERN = re.compile(
    r"<start_function_call>call:select_(?:rpr|blm|mch|vpr|sam)_action\{(.*?)\}<end_function_call>",
    re.DOTALL,
)
ARGUMENT_PATTERN = re.compile(r"(\w+):(?:<escape>(.*?)<escape>|([^,}]*))", re.DOTALL)


def parse_function_call(text):
    match = FUNCTION_CALL_PATTERN.search(text)
    if not match:
        return None

    arguments = {}
    for name, escaped, plain in ARGUMENT_PATTERN.findall(match.group(1)):
        value = (escaped or plain).strip().strip("'\"")
        arguments[name] = value

    action = arguments.get("action", "")
    try:
        confidence = int(arguments.get("confidence", "0"))
    except ValueError:
        confidence = 0
    return action, confidence


def protected_baseline(job, baseline):
    return (baseline, 100) if job == "SAM" else None


class FunctionGemmaModel:
    MAX_CANDIDATES = 4
    SCORE_TEMPERATURE = 2.0

    def __init__(self, model_id):
        import torch
        from transformers import AutoModelForCausalLM, AutoProcessor

        self._torch = torch
        self._processor = AutoProcessor.from_pretrained(model_id, device_map="auto")
        self._tokenizer = getattr(self._processor, "tokenizer", self._processor)
        self._model = AutoModelForCausalLM.from_pretrained(model_id, dtype="auto", device_map="auto")
        self._model.eval()
        self._lock = threading.Lock()
        self.decide(
            {
                "job": "RPR",
                "baseline": "Slice",
                "state": {},
                "candidates": [
                    {"action": "Slice", "priorityDelta": 0, "baseline": True},
                    {"action": "ShadowOfDeath", "priorityDelta": 0, "baseline": False},
                ],
            }
        )

    def decide(self, request):
        candidates = request.get("candidates", [])
        allowed = [candidate.get("action", "") for candidate in candidates]
        baseline = request.get("baseline", "")
        job = request.get("job", "RPR").upper()
        if job not in ("RPR", "BLM", "MCH", "VPR", "SAM") or baseline not in allowed or len(allowed) < 2 or len(allowed) > self.MAX_CANDIDATES:
            return baseline, 0

        protected = protected_baseline(job, baseline)
        if protected is not None:
            return protected

        function_name = f"select_{job.lower()}_action"
        if job == "RPR":
            description = (
                "Select exactly one legal Reaper action from the supplied candidates. "
                "Preserve GCD uptime, combo, Death's Design, burst resources, and expiring procs. "
                "Choose the baseline unless another candidate has a clear expected-DPS advantage."
            )
            objective = "maximize expected RPR damage without violating the existing legal rotation candidates"
        elif job == "BLM":
            description = (
                "Select exactly one legal Black Mage action from the supplied candidates. "
                "Preserve GCD uptime, Astral Fire and Umbral Ice flow, MP, Astral Soul, Polyglot, "
                "Thunder, Manafont sequencing, movement instants, and expiring procs. "
                "Choose the baseline unless another candidate has a clear expected-DPS advantage."
            )
            objective = "maximize expected BLM damage without violating the existing legal rotation candidates"
        elif job == "MCH":
            description = (
                "Select exactly one legal Machinist action from the supplied candidates. "
                "Preserve GCD uptime, combo, tool charge caps, Heat and Battery, Wildfire and Hypercharge sequencing, "
                "Reassemble, Automaton Queen, Excavator, and expiring Full Metal Field. "
                "Choose the baseline unless another candidate has a clear expected-DPS advantage."
            )
            objective = "maximize expected MCH damage without violating the existing legal rotation candidates"
        elif job == "VPR":
            description = (
                "Select exactly one legal Viper combo action from the supplied candidates. "
                "Preserve GCD uptime, combo progression, Hunter's Instinct, Swiftscaled, Honed Steel, Honed Reavers, venom-enhanced finishers, "
                "Serpent Offering, Rattling Coil, Vicewinder preparation, and Reawaken sequencing. "
                "Choose the baseline unless another candidate has a clear expected-DPS advantage."
            )
            objective = "maximize expected VPR damage without violating the existing legal rotation candidates"
        else:
            description = (
                "Select exactly one legal Samurai action from the supplied candidates. "
                "Preserve GCD uptime, combo, Sen composition, Kenki and Meditation, Fugetsu and Fuka, Higanbana, "
                "Meikyo and Tendo sequencing, Tsubame and Kaeshi readiness, Ogi Namikiri, Zanshin, and burst resources. "
                "Choose the baseline unless another candidate has a clear expected-DPS advantage."
            )
            objective = "maximize expected SAM damage without violating the existing legal rotation candidates"

        tools = [
            {
                "type": "function",
                "function": {
                    "name": function_name,
                    "description": description,
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "action": {
                                "type": "string",
                                "enum": allowed,
                                "description": "The exact action name to execute.",
                            },
                            "confidence": {
                                "type": "integer",
                                "minimum": 0,
                                "maximum": 100,
                                "description": "Confidence that this is better than the baseline.",
                            },
                        },
                        "required": ["action", "confidence"],
                    },
                },
            }
        ]
        messages = [
            {
                "role": "developer",
                "content": "You are a model that can do function calling with the following functions",
            },
            {
                "role": "user",
                "content": json.dumps(
                    {
                        "job": job,
                        "objective": objective,
                        "baseline": baseline,
                        "state": request.get("state", {}),
                        "candidates": candidates,
                    },
                    separators=(",", ":"),
                ),
            },
        ]

        with self._lock, self._torch.inference_mode():
            prompt = self._processor.apply_chat_template(
                messages,
                tools=tools,
                add_generation_prompt=True,
                return_dict=True,
                return_tensors="pt",
            )
            prompt_ids = prompt["input_ids"][0].tolist()
            prefix_ids = self._tokenizer.encode(
                f"<start_function_call>call:{function_name}{{action:<escape>",
                add_special_tokens=False,
            )
            suffix_ids = self._tokenizer.encode(
                "<escape>,confidence:100}<end_function_call>",
                add_special_tokens=False,
            )
            sequences = []
            action_ranges = []
            for action in allowed:
                action_ids = self._tokenizer.encode(action, add_special_tokens=False)
                if not action_ids:
                    return baseline, 0
                action_start = len(prompt_ids) + len(prefix_ids)
                sequences.append(prompt_ids + prefix_ids + action_ids + suffix_ids)
                action_ranges.append((action_start, action_start + len(action_ids)))

            maximum_length = max(len(sequence) for sequence in sequences)
            pad_token_id = self._tokenizer.pad_token_id
            if pad_token_id is None:
                pad_token_id = self._tokenizer.eos_token_id
            input_ids = self._torch.tensor(
                [sequence + [pad_token_id] * (maximum_length - len(sequence)) for sequence in sequences],
                device=self._model.device,
            )
            attention_mask = self._torch.tensor(
                [[1] * len(sequence) + [0] * (maximum_length - len(sequence)) for sequence in sequences],
                device=self._model.device,
            )
            logits = self._model(input_ids=input_ids, attention_mask=attention_mask).logits
            log_probabilities = self._torch.log_softmax(logits.float(), dim=-1)
            scores = []
            for batch_index, (action_start, action_end) in enumerate(action_ranges):
                token_scores = [
                    log_probabilities[batch_index, token_index - 1, input_ids[batch_index, token_index]]
                    for token_index in range(action_start, action_end)
                ]
                scores.append(self._torch.stack(token_scores).mean())

            probabilities = self._torch.softmax(self._torch.stack(scores) * self.SCORE_TEMPERATURE, dim=0)
            selected_index = int(self._torch.argmax(probabilities).item())
            confidence = int(round(float(probabilities[selected_index].item()) * 100))
            return allowed[selected_index], max(0, min(100, confidence))


class DecisionHandler(BaseHTTPRequestHandler):
    model = None

    def do_GET(self):
        if self.path != "/health":
            self.send_error(404)
            return
        self._json_response(200, {"ready": True})

    def do_POST(self):
        if self.path != "/decide":
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > 65536:
                self.send_error(400)
                return
            request = json.loads(self.rfile.read(length))
            started = time.perf_counter()
            action, confidence = self.model.decide(request)
            self._json_response(
                200,
                {
                    "request_id": request.get("requestID", ""),
                    "action": action,
                    "confidence": confidence,
                    "latency_ms": round((time.perf_counter() - started) * 1000, 3),
                },
            )
        except (json.JSONDecodeError, TypeError, ValueError):
            self.send_error(400)
        except Exception:
            self.send_error(500)

    def log_message(self, _format, *_args):
        return

    def _json_response(self, status, payload):
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def self_test():
    valid = "<start_function_call>call:select_rpr_action{action:<escape>Gluttony<escape>,confidence:98}<end_function_call>"
    assert parse_function_call(valid) == ("Gluttony", 98)
    valid_blm = "<start_function_call>call:select_blm_action{action:<escape>FlareStar<escape>,confidence:99}<end_function_call>"
    assert parse_function_call(valid_blm) == ("FlareStar", 99)
    valid_mch = "<start_function_call>call:select_mch_action{action:<escape>FullMetalField<escape>,confidence:99}<end_function_call>"
    assert parse_function_call(valid_mch) == ("FullMetalField", 99)
    valid_vpr = "<start_function_call>call:select_vpr_action{action:<escape>SteelFangs<escape>,confidence:99}<end_function_call>"
    assert parse_function_call(valid_vpr) == ("SteelFangs", 99)
    valid_sam = "<start_function_call>call:select_sam_action{action:<escape>MidareSetsugekka<escape>,confidence:99}<end_function_call>"
    assert parse_function_call(valid_sam) == ("MidareSetsugekka", 99)
    assert protected_baseline("SAM", "Kasha") == ("Kasha", 100)
    assert protected_baseline("SAM", "HissatsuSenei") == ("HissatsuSenei", 100)
    assert protected_baseline("MCH", "Drill") is None
    assert parse_function_call("invalid") is None


def main():
    parser = argparse.ArgumentParser(description="Loopback-only FunctionGemma supervisor for BossMod RPR, BLM, MCH, and VPR with a SAM baseline guard")
    parser.add_argument("--model", default="google/functiongemma-270m-it")
    parser.add_argument("--port", type=int, default=8089)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return

    DecisionHandler.model = FunctionGemmaModel(args.model)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), DecisionHandler)
    server.serve_forever()


if __name__ == "__main__":
    main()
