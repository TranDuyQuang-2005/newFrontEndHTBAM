from __future__ import annotations
import numpy as np
from sklearn.metrics import accuracy_score, balanced_accuracy_score, f1_score, precision_recall_fscore_support, confusion_matrix


def classification_metrics(y_true, y_pred, num_classes: int = 4) -> dict:
    y_true=np.asarray(y_true); y_pred=np.asarray(y_pred)
    if y_true.size == 0:
        return {"accuracy":0.0,"balanced_accuracy":0.0,"macro_f1":0.0,"confusion_matrix":np.zeros((num_classes,num_classes),dtype=int).tolist()}
    p,r,f,_=precision_recall_fscore_support(y_true,y_pred,labels=list(range(num_classes)),zero_division=0)
    return {
        "accuracy":float(accuracy_score(y_true,y_pred)),
        "balanced_accuracy":float(balanced_accuracy_score(y_true,y_pred)),
        "macro_f1":float(f1_score(y_true,y_pred,average="macro",labels=list(range(num_classes)),zero_division=0)),
        "per_class_precision":p.tolist(),"per_class_recall":r.tolist(),"per_class_f1":f.tolist(),
        "confusion_matrix":confusion_matrix(y_true,y_pred,labels=list(range(num_classes))).tolist(),
    }
