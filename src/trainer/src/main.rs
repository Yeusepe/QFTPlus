use meganeura::{Graph, NodeId, Session, SessionConfig};
use serde_json::{Value, json};
use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};
use std::time::Instant;

fn floats(path: &Path) -> Vec<f32> {
    std::fs::read(path)
        .unwrap_or_else(|e| fail(&format!("{}: {e}", path.display())))
        .chunks_exact(4)
        .map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]]))
        .collect()
}

fn write(path: &Path, values: &[f32]) {
    std::fs::write(path, values.iter().flat_map(|v| v.to_le_bytes()).collect::<Vec<u8>>())
        .unwrap_or_else(|e| fail(&format!("{}: {e}", path.display())));
}

fn fail(message: &str) -> ! {
    println!("{}", json!({ "error": message }));
    std::process::exit(2)
}

#[derive(Clone)]
struct Spec {
    model: String,
    batch: u32,
    targets: usize,
    size: u32,
    frozen_blocks: usize,
    pairs: usize,
    sides: usize,
}

fn conv(g: &mut Graph, x: NodeId, name: &str, n: u32, cin: u32, hw: u32, cout: u32, k: u32, s: u32, p: u32) -> (NodeId, u32) {
    let w = g.parameter(&format!("{name}.w"), &[(cout * cin * k * k) as usize]);
    let y = g.conv2d(x, w, n, cin, hw, hw, cout, k, k, s, p);
    let out = (hw + 2 * p - k) / s + 1;
    let b = g.parameter(&format!("{name}.b"), &[cout as usize]);
    (g.add_per_channel(y, b, cout, out * out), out)
}

fn residual(g: &mut Graph, x: NodeId, name: &str, n: u32, c: u32, hw: u32) -> NodeId {
    let (a, _) = conv(g, x, &format!("{name}.network.0"), n, c, hw, c, 3, 1, 1);
    let a = g.silu(a);
    let (b, _) = conv(g, a, &format!("{name}.network.3"), n, c, hw, c, 3, 1, 1);
    let sum = g.add(x, b);
    g.silu(sum)
}

fn linear(g: &mut Graph, x: NodeId, name: &str, i: usize, o: usize) -> NodeId {
    let w = g.parameter(&format!("{name}.w"), &[i, o]);
    let b = g.parameter(&format!("{name}.b"), &[o]);
    let y = g.matmul(x, w);
    g.bias_add(y, b)
}

fn encoder(g: &mut Graph, x: NodeId, prefix: &str, n: u32, size: u32, frozen: usize) -> (NodeId, u32) {
    let (h, s) = conv(g, x, &format!("{prefix}.network.0"), n, 1, size, 32, 5, 2, 2);
    let h = g.silu(h);
    let mut h = residual(g, h, &format!("{prefix}.network.3"), n, 32, s);
    if frozen == 1 {
        h = g.stop_gradient(h);
    }
    let mut s = s;
    for (i, (cin, cout)) in [(32u32, 64u32), (64, 96), (96, 160)].into_iter().enumerate() {
        let (c, out) = conv(g, h, &format!("{prefix}.network.{}", 4 + 4 * i), n, cin, s, cout, 3, 2, 1);
        let c = g.silu(c);
        h = residual(g, c, &format!("{prefix}.network.{}", 7 + 4 * i), n, cout, out);
        s = out;
        if frozen == i + 2 {
            h = g.stop_gradient(h);
        }
    }
    (h, s)
}

fn forward(g: &mut Graph, spec: &Spec, batch: u32) -> (NodeId, NodeId) {
    let b = batch as usize;
    let size = spec.size;
    let x = g.input("x", &[b * 2 * (size * size) as usize]);
    match spec.model.as_str() {
        "tongue" => {
            let (h, s) = encoder(g, x, "encoder", batch * 2, size, spec.frozen_blocks);
            let area = s * s;
            let left = g.split_a(h, batch, 160, 160, area);
            let right = g.split_b(h, batch, 160, 160, area);
            let negative = g.neg(right);
            let difference = g.add(left, negative);
            let difference = g.abs(difference);
            let product = g.mul(left, right);
            let pair = g.concat(left, right, batch, 160, 160, area);
            let cues = g.concat(difference, product, batch, 160, 160, area);
            let stereo = g.concat(pair, cues, batch, 320, 320, area);
            let (f, _) = conv(g, stereo, "stereo_fusion.0", batch, 640, s, 224, 1, 1, 0);
            let f = g.silu(f);
            let f = residual(g, f, "stereo_fusion.3", batch, 224, s);
            let (f, s2) = conv(g, f, "stereo_fusion.4", batch, 224, s, 256, 3, 2, 1);
            let f = g.silu(f);
            let f = residual(g, f, "stereo_fusion.7", batch, 256, s2);
            let average = g.global_avg_pool(f, batch, 256, s2 * s2);
            let average = g.reshape(average, &[b * 256]);
            let maximum = g.max_pool_2d(f, batch, 256, s2, s2, s2, s2, s2, 0);
            let pooled = g.concat(average, maximum, batch, 256, 256, 1);
            let pooled = g.reshape(pooled, &[b, 512]);
            let z = linear(g, pooled, "head.0", 512, 384);
            let z = g.silu(z);
            let z = linear(g, z, "head.3", 384, 192);
            let z = g.silu(z);
            let logits = linear(g, z, "head.6", 192, spec.targets);
            let signed = g.input("signed", &[b, spec.targets]);
            let one = g.constant(vec![1.0; b * spec.targets], &[b, spec.targets]);
            let sigmoid = g.sigmoid(logits);
            let tanh = g.tanh(logits);
            let negative_signed = g.neg(signed);
            let unsigned = g.add(one, negative_signed);
            let a = g.mul(tanh, signed);
            let c = g.mul(sigmoid, unsigned);
            (g.add(a, c), logits)
        }
        "mouth" => {
            let scaled = g.scale(x, 4.0);
            let shift = g.constant(vec![-1.0; b * 2 * (size * size) as usize], &[b * 2 * (size * size) as usize]);
            let normalized = g.add(scaled, shift);
            let (h, s) = encoder(g, normalized, "encoder", batch * 2, size, spec.frozen_blocks);
            let pooled = g.global_avg_pool(h, batch * 2, 160, s * s);
            let pooled = g.reshape(pooled, &[b, 320]);
            let logits = linear(g, pooled, "head", 320, spec.targets);
            (g.sigmoid(logits), logits)
        }
        other => fail(&format!("unknown model {other}")),
    }
}

fn training_graph(spec: &Spec) -> Graph {
    let mut g = Graph::new();
    let (b, t) = (spec.batch as usize, spec.targets);
    let (p, logits) = forward(&mut g, spec, spec.batch);
    let y = g.input("y", &[b, t]);
    let loss = match spec.model.as_str() {
        "tongue" => {
            let cv = g.input("cv", &[b, t]);
            let wn = g.input("wn", &[b, t]);
            let soft = g.softplus(logits, 1.0);
            let yz = g.mul(y, logits);
            let negative_yz = g.neg(yz);
            let bce = g.add(soft, negative_yz);
            let bce = g.mul(bce, cv);
            let bce = g.sum_all(bce);
            let beta = 0.08;
            let beta_tensor = g.constant(vec![beta; b * t], &[b, t]);
            let negative_y = g.neg(y);
            let d = g.add(p, negative_y);
            let ad = g.abs(d);
            let negative_ad = g.neg(ad);
            let gap = g.add(beta_tensor, negative_ad);
            let gap = g.relu(gap);
            let negative_gap = g.neg(gap);
            let m = g.add(beta_tensor, negative_gap);
            let m2 = g.mul(m, m);
            let quadratic = g.scale(m2, 0.5 / beta);
            let negative_m = g.neg(m);
            let linear_part = g.add(ad, negative_m);
            let huber = g.add(quadratic, linear_part);
            let huber = g.mul(huber, wn);
            let regression = g.sum_all(huber);
            g.add(bce, regression)
        }
        "mouth" => {
            let wl = g.input("wl", &[b, t]);
            let negative_y = g.neg(y);
            let d = g.add(p, negative_y);
            let d2 = g.mul(d, d);
            let level = g.mul(d2, wl);
            let level = g.sum_all(level);
            let flat = g.reshape(p, &[b * t, 1]);
            let pairs = g.input("pairs", &[spec.pairs, b * t]);
            let gaps = g.input("gaps", &[spec.pairs, 1]);
            let pair_weights = g.input("pair_weights", &[spec.pairs, 1]);
            let separation = g.matmul(pairs, flat);
            let negative_separation = g.neg(separation);
            let hinge = g.add(gaps, negative_separation);
            let hinge = g.relu(hinge);
            let hinge2 = g.mul(hinge, hinge);
            let ordinal = g.mul(hinge2, pair_weights);
            let ordinal = g.sum_all(ordinal);
            let sides = g.input("sides", &[spec.sides, b * t]);
            let side_targets = g.input("side_targets", &[spec.sides, 1]);
            let side_weights = g.input("side_weights", &[spec.sides, 1]);
            let delta = g.matmul(sides, flat);
            let negative_target = g.neg(side_targets);
            let error = g.add(delta, negative_target);
            let error2 = g.mul(error, error);
            let side = g.mul(error2, side_weights);
            let side = g.sum_all(side);
            let total = g.add(level, ordinal);
            g.add(total, side)
        }
        _ => unreachable!(),
    };
    g.set_outputs(vec![loss]);
    g
}

fn input_names(spec: &Spec) -> Vec<&'static str> {
    match spec.model.as_str() {
        "tongue" => vec!["x", "y", "cv", "wn", "signed"],
        _ => vec!["x", "y", "wl", "pairs", "gaps", "pair_weights", "sides", "side_targets", "side_weights"],
    }
}

struct Trainer {
    spec: Spec,
    session: Session,
    eval: Session,
    eval_batch: u32,
    names: Vec<String>,
    shapes: serde_json::Map<String, Value>,
    signed: Vec<f32>,
}

fn init(command: &Value) -> (Trainer, Value) {
    let text = |k: &str| command[k].as_str().unwrap_or_else(|| fail(&format!("missing {k}"))).to_string();
    let number = |k: &str| command[k].as_u64().unwrap_or_else(|| fail(&format!("missing {k}"))) as usize;
    let spec = Spec {
        model: text("model"),
        batch: number("batch") as u32,
        targets: number("targets"),
        size: number("size") as u32,
        frozen_blocks: number("frozen_blocks"),
        pairs: command["pairs"].as_u64().unwrap_or(1) as usize,
        sides: command["sides"].as_u64().unwrap_or(1) as usize,
    };
    let eval_batch = number("eval_batch") as u32;
    let params = PathBuf::from(text("params"));
    let cache = command["cache"].as_str().map(PathBuf::from);
    let started = Instant::now();
    let graph = training_graph(&spec);
    let mut session = meganeura::build(&graph, SessionConfig { cache: cache.as_deref().map(|p| p.join(format!("{}-train.plan", spec.model))).as_deref(), ..SessionConfig::from_env() }).0;
    let mut evaluation = Graph::new();
    let (prediction, _) = forward(&mut evaluation, &spec, eval_batch);
    evaluation.set_outputs(vec![prediction]);
    let eval = meganeura::build(&evaluation, SessionConfig {
        mode: meganeura::Mode::Inference,
        share_parameters_from: Some(&mut session),
        ..SessionConfig::from_env()
    }).0;
    let shapes: serde_json::Map<String, Value> =
        serde_json::from_str(&std::fs::read_to_string(params.join("manifest.json")).unwrap_or_else(|e| fail(&e.to_string())))
            .unwrap_or_else(|e| fail(&e.to_string()));
    let names: Vec<String> = shapes.keys().cloned().collect();
    for name in &names {
        if !session.has_parameter(name) {
            fail(&format!("model has no parameter {name}"));
        }
        session.set_parameter(name, &floats(&params.join(format!("{name}.bin"))));
    }
    for name in session.param_names().iter().map(|n| n.to_string()).collect::<Vec<_>>() {
        if !shapes.contains_key(&name) {
            fail(&format!("no weights for parameter {name}"));
        }
    }
    let lr = command["lr"].as_f64().unwrap_or(1e-3) as f32;
    session.set_adam(lr, 0.9, 0.999, 1e-8);
    session.set_weight_decay(command["weight_decay"].as_f64().unwrap_or(0.0) as f32);
    let signed = command["signed"].as_array().map(|v| v.iter().map(|x| x.as_f64().unwrap_or(0.0) as f32).collect()).unwrap_or_default();
    let reply = json!({ "ok": true, "compileSeconds": started.elapsed().as_secs_f32(), "parameters": names.len() });
    (Trainer { spec, session, eval, eval_batch, names, shapes, signed }, reply)
}

impl Trainer {
    fn signed_rows(&self, rows: usize) -> Vec<f32> {
        (0..rows).flat_map(|_| self.signed.iter().copied()).collect()
    }

    fn step(&mut self, dir: &Path) -> Value {
        let started = Instant::now();
        for name in input_names(&self.spec) {
            let values = if name == "signed" { self.signed_rows(self.spec.batch as usize) } else { floats(&dir.join(format!("{name}.bin"))) };
            self.session.set_input(name, &values);
        }
        self.session.step();
        self.session.wait();
        let loss = self.session.read_loss();
        if !loss.is_finite() {
            fail("non-finite loss");
        }
        json!({ "loss": loss, "seconds": started.elapsed().as_secs_f32() })
    }

    fn predict(&mut self, x: &Path, out: &Path) -> Value {
        let values = floats(x);
        let per = 2 * (self.spec.size * self.spec.size) as usize;
        let count = values.len() / per;
        let eval = self.eval_batch as usize;
        let mut result = Vec::with_capacity(count * self.spec.targets);
        let mut chunk = vec![0f32; eval * per];
        let mut output = vec![0f32; eval * self.spec.targets];
        for start in (0..count).step_by(eval) {
            let n = eval.min(count - start);
            chunk.fill(0.0);
            chunk[..n * per].copy_from_slice(&values[start * per..(start + n) * per]);
            self.eval.set_input("x", &chunk);
            if self.spec.model == "tongue" {
                let rows = self.signed_rows(eval);
                self.eval.set_input("signed", &rows);
            }
            self.eval.step();
            self.eval.wait();
            self.eval.read_output_by_index(0, &mut output);
            result.extend_from_slice(&output[..n * self.spec.targets]);
        }
        write(out, &result);
        json!({ "ok": true, "count": count })
    }

    fn dump(&self, dir: &Path, gradients: bool) -> Value {
        std::fs::create_dir_all(dir).unwrap_or_else(|e| fail(&e.to_string()));
        let mut written = vec![];
        for name in &self.names {
            let len: usize = self.shapes[name].as_array().unwrap().iter().map(|v| v.as_u64().unwrap() as usize).product();
            let mut values = vec![0f32; len];
            if gradients {
                if std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| self.session.read_param_grad(name, &mut values))).is_err() {
                    continue;
                }
            } else {
                self.session.read_param(name, &mut values);
            }
            write(&dir.join(format!("{name}.bin")), &values);
            written.push(name.clone());
        }
        json!({ "ok": true, "written": written })
    }
}

fn main() {
    std::panic::set_hook(Box::new(|info| eprintln!("qft-trainer: {info}")));
    let stdin = std::io::stdin();
    let mut trainer: Option<Trainer> = None;
    for line in stdin.lock().lines() {
        let line = line.unwrap_or_else(|e| fail(&e.to_string()));
        if line.trim().is_empty() {
            continue;
        }
        let command: Value = serde_json::from_str(&line).unwrap_or_else(|e| fail(&e.to_string()));
        let path = |k: &str| PathBuf::from(command[k].as_str().unwrap_or_else(|| fail(&format!("missing {k}"))));
        let reply = match command["cmd"].as_str() {
            Some("init") => {
                let (built, reply) = init(&command);
                trainer = Some(built);
                reply
            }
            Some("quit") => break,
            Some(cmd) => {
                let t = trainer.as_mut().unwrap_or_else(|| fail("init first"));
                match cmd {
                    "step" => t.step(&path("dir")),
                    "predict" => t.predict(&path("x"), &path("out")),
                    "save" => t.dump(&path("dir"), false),
                    "grads" => t.dump(&path("dir"), true),
                    other => fail(&format!("unknown command {other}")),
                }
            }
            None => fail("missing cmd"),
        };
        println!("{reply}");
        std::io::stdout().flush().ok();
    }
}
