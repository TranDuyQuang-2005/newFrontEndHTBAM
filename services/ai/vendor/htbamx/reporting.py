from __future__ import annotations
from pathlib import Path
import csv
import json
import shutil
from typing import Iterable

import matplotlib
matplotlib.use("Agg", force=True)  # headless/file-only reporting; avoids Tk/Tcl dependency
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from sklearn.metrics import classification_report, confusion_matrix

from .behavior_cache import BEHAVIORS


def reset_output_dir(out_dir: str | Path) -> Path:
    """Start a fresh training result directory.

    Retraining intentionally replaces the previous run. Dangerous top-level
    directories are refused to avoid accidental deletion.
    """
    out = Path(out_dir).resolve()
    dangerous = {Path('/').resolve(), Path.home().resolve(), Path.cwd().resolve()}
    if out in dangerous or out.name in {'', '.', '..', 'checkpoints', 'models'}:
        raise ValueError(f"Refusing to reset unsafe output directory: {out}")
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True, exist_ok=True)
    (out / 'plots').mkdir(parents=True, exist_ok=True)
    (out / 'reports').mkdir(parents=True, exist_ok=True)
    return out


def save_json(path: str | Path, obj: dict) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('w', encoding='utf-8') as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)


def save_training_curves(history: pd.DataFrame, out_dir: str | Path) -> None:
    out = Path(out_dir) / 'plots'
    out.mkdir(parents=True, exist_ok=True)
    if history.empty:
        return

    def plot_two(train_col: str, val_col: str, ylabel: str, filename: str):
        if train_col not in history.columns and val_col not in history.columns:
            return
        fig = plt.figure(figsize=(8, 5))
        ax = fig.add_subplot(111)
        if train_col in history.columns:
            ax.plot(history['epoch'], history[train_col], label='Train')
        if val_col in history.columns:
            ax.plot(history['epoch'], history[val_col], label='Validation')
        ax.set_xlabel('Epoch')
        ax.set_ylabel(ylabel)
        ax.set_title(f'{ylabel} by Epoch')
        ax.grid(True, alpha=0.25)
        ax.legend()
        fig.tight_layout()
        fig.savefig(out / filename, dpi=180)
        plt.close(fig)

    plot_two('train_loss', 'val_loss', 'Loss', 'loss_curve.png')
    plot_two('train_accuracy', 'val_accuracy', 'Accuracy', 'accuracy_curve.png')
    plot_two('train_macro_f1', 'val_macro_f1', 'Macro-F1', 'macro_f1_curve.png')
    plot_two('train_balanced_accuracy', 'val_balanced_accuracy', 'Balanced Accuracy', 'balanced_accuracy_curve.png')

    if 'lr' in history.columns:
        fig = plt.figure(figsize=(8, 5))
        ax = fig.add_subplot(111)
        ax.plot(history['epoch'], history['lr'])
        ax.set_xlabel('Epoch')
        ax.set_ylabel('Learning rate')
        ax.set_title('Learning Rate by Epoch')
        ax.grid(True, alpha=0.25)
        fig.tight_layout()
        fig.savefig(out / 'learning_rate_curve.png', dpi=180)
        plt.close(fig)


def save_confusion_artifacts(y_true: Iterable[int], y_pred: Iterable[int], split: str, out_dir: str | Path,
                             labels: list[str] | None = None) -> None:
    labels = labels or BEHAVIORS
    y_true = np.asarray(list(y_true), dtype=int)
    y_pred = np.asarray(list(y_pred), dtype=int)
    ids = list(range(len(labels)))
    cm = confusion_matrix(y_true, y_pred, labels=ids) if y_true.size else np.zeros((len(labels), len(labels)), dtype=int)

    reports = Path(out_dir) / 'reports'
    plots = Path(out_dir) / 'plots'
    reports.mkdir(parents=True, exist_ok=True)
    plots.mkdir(parents=True, exist_ok=True)

    pd.DataFrame(cm, index=labels, columns=labels).to_csv(reports / f'{split}_confusion_matrix.csv')

    # Row-normalized confusion matrix is often easier to interpret in reports.
    row_sum = cm.sum(axis=1, keepdims=True)
    cm_norm = np.divide(cm, row_sum, out=np.zeros_like(cm, dtype=float), where=row_sum != 0)
    pd.DataFrame(cm_norm, index=labels, columns=labels).to_csv(reports / f'{split}_confusion_matrix_normalized.csv')
    fig = plt.figure(figsize=(7, 6))
    ax = fig.add_subplot(111)
    im = ax.imshow(cm_norm, vmin=0.0, vmax=1.0)
    ax.set_xticks(range(len(labels)), labels=labels, rotation=35, ha='right')
    ax.set_yticks(range(len(labels)), labels=labels)
    ax.set_xlabel('Predicted')
    ax.set_ylabel('True')
    ax.set_title(f'{split.capitalize()} Normalized Confusion Matrix')
    for i in range(cm_norm.shape[0]):
        for j in range(cm_norm.shape[1]):
            ax.text(j, i, f'{cm_norm[i, j]:.2f}', ha='center', va='center')
    fig.colorbar(im, ax=ax)
    fig.tight_layout()
    fig.savefig(plots / f'{split}_confusion_matrix_normalized.png', dpi=180)
    plt.close(fig)

    report = classification_report(
        y_true, y_pred, labels=ids, target_names=labels,
        output_dict=True, zero_division=0
    ) if y_true.size else {name: {'precision':0.0,'recall':0.0,'f1-score':0.0,'support':0.0} for name in labels}
    pd.DataFrame(report).T.to_csv(reports / f'{split}_classification_report.csv')

    fig = plt.figure(figsize=(7, 6))
    ax = fig.add_subplot(111)
    im = ax.imshow(cm)
    ax.set_xticks(range(len(labels)), labels=labels, rotation=35, ha='right')
    ax.set_yticks(range(len(labels)), labels=labels)
    ax.set_xlabel('Predicted')
    ax.set_ylabel('True')
    ax.set_title(f'{split.capitalize()} Confusion Matrix')
    for i in range(cm.shape[0]):
        for j in range(cm.shape[1]):
            ax.text(j, i, str(int(cm[i, j])), ha='center', va='center')
    fig.colorbar(im, ax=ax)
    fig.tight_layout()
    fig.savefig(plots / f'{split}_confusion_matrix.png', dpi=180)
    plt.close(fig)

    # Per-class F1 figure for report insertion.
    f1s = [float(report.get(name, {}).get('f1-score', 0.0)) for name in labels]
    fig = plt.figure(figsize=(8, 5))
    ax = fig.add_subplot(111)
    ax.bar(labels, f1s)
    ax.set_ylim(0, 1)
    ax.set_ylabel('F1-score')
    ax.set_title(f'{split.capitalize()} Per-class F1')
    ax.tick_params(axis='x', rotation=25)
    fig.tight_layout()
    fig.savefig(plots / f'{split}_per_class_f1.png', dpi=180)
    plt.close(fig)


def save_predictions(y_true: Iterable[int], y_pred: Iterable[int], split: str, out_dir: str | Path,
                     labels: list[str] | None = None) -> None:
    labels = labels or BEHAVIORS
    rows=[]
    for i,(yt,yp) in enumerate(zip(y_true,y_pred)):
        rows.append({
            'sample_index': i,
            'true_id': int(yt),
            'true_label': labels[int(yt)] if 0 <= int(yt) < len(labels) else str(yt),
            'pred_id': int(yp),
            'pred_label': labels[int(yp)] if 0 <= int(yp) < len(labels) else str(yp),
            'correct': int(yt) == int(yp),
        })
    pd.DataFrame(rows).to_csv(Path(out_dir)/'reports'/f'{split}_predictions.csv', index=False)
