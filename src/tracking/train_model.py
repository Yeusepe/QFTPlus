"""Five-camera architecture used by the calibrated expression heads."""

from __future__ import annotations

import torch
from torch import nn


class CameraEncoder(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        channels = (1, 16, 32, 48, 64)
        layers: list[nn.Module] = []
        for input_channels, output_channels in zip(channels, channels[1:]):
            layers.extend(
                [
                    nn.Conv2d(
                        input_channels, output_channels, 3, stride=2, padding=1,
                        bias=False,
                    ),
                    nn.BatchNorm2d(output_channels),
                    nn.SiLU(inplace=True),
                ]
            )
        layers.append(nn.AdaptiveAvgPool2d(1))
        self.network = nn.Sequential(*layers)

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        return self.network(image).flatten(1)


class QuestProTrackingModel(nn.Module):
    """Separate symmetric encoders with late feature fusion and two heads."""

    def __init__(self, expression_count: int = 70) -> None:
        super().__init__()
        self.eye_encoder = CameraEncoder()
        self.batch_views = False
        self.face_encoder = CameraEncoder()
        self.brow_encoder = CameraEncoder()
        self.fusion = nn.Sequential(
            nn.Linear(64 * 5, 256),
            nn.SiLU(inplace=True),
            nn.Dropout(0.10),
            nn.Linear(256, 160),
            nn.SiLU(inplace=True),
        )
        self.expression_head = nn.Linear(160, expression_count)
        self.eye_orientation_head = nn.Sequential(
            nn.Linear(64 * 2, 96), nn.SiLU(inplace=True), nn.Linear(96, 8)
        )

    def forward(self, cameras: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        if self.batch_views and not self.training:
            eyes = self.eye_encoder(cameras[:, :2].flatten(0, 1).unsqueeze(1))
            faces = self.face_encoder(cameras[:, 2:4].flatten(0, 1).unsqueeze(1))
            left_eye, right_eye = eyes[0::2], eyes[1::2]
            left_face, right_face = faces[0::2], faces[1::2]
        else:
            left_eye = self.eye_encoder(cameras[:, 0:1])
            right_eye = self.eye_encoder(cameras[:, 1:2])
            left_face = self.face_encoder(cameras[:, 2:3])
            right_face = self.face_encoder(cameras[:, 3:4])
        brow = self.brow_encoder(cameras[:, 4:5])
        fused = self.fusion(
            torch.cat((left_eye, right_eye, left_face, right_face, brow), dim=1)
        )
        expressions = torch.sigmoid(self.expression_head(fused))
        orientations = self.eye_orientation_head(torch.cat((left_eye, right_eye), dim=1))
        return expressions, nn.functional.normalize(orientations.reshape(-1, 2, 4), dim=-1).flatten(1)
